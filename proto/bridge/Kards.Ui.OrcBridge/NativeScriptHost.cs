using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Orc.Core;
using Orc.Script;

namespace Kards.Ui.OrcBridge;

/// <summary>Host adapter for Godot's in-memory assemblies; the engine still mounts and executes effects.</summary>
public static class NativeScriptHost
{
    private static IScriptEvaluator _evaluator = new HostEvaluator(null);

    /// <summary>Only the UI host supplies the directory of its current managed build.</summary>
    public static void ConfigureAssemblyDirectory(string directory)
        => Volatile.Write(ref _evaluator, new HostEvaluator(Path.GetFullPath(directory)));

    internal static IScriptEvaluator Evaluator => Volatile.Read(ref _evaluator);

    private sealed class HostEvaluator(string? directory) : IScriptEvaluator
    {
        private readonly CSharpScriptSecurityOptions _policy = new();
        private readonly ConcurrentDictionary<ScriptRequest, ScriptEvaluationResult> _cache = new();
        private readonly Lazy<ScriptOptions> _runtime = new(() => Build(directory));

        public ScriptEvaluationResult Evaluate(ScriptRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return _cache.GetOrAdd(request, Compile);
        }

        private ScriptEvaluationResult Compile(ScriptRequest request)
        {
            foreach (var fragment in _policy.BlockedKeywords)
                if (!string.IsNullOrEmpty(fragment) && request.Source.Contains(fragment, StringComparison.Ordinal))
                    return ScriptEvaluationResult.Fail("sandbox", $"脚本命中屏蔽片段 '{fragment}'");
            try
            {
                using var reflectionScope = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(NativeScriptHost).Assembly)
                    ?.EnterContextualReflection();
                var script = CSharpScript.Create(request.Source, _runtime.Value);
                var compilation = script.GetCompilation();
                using var binary = new MemoryStream();
                var emitted = compilation.Emit(binary);
                var errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                if (errors.Length > 0)
                    return ScriptEvaluationResult.Fail("compile", string.Join("；", errors.Select(e => e.GetMessage())));
                // Roslyn's default script runner loads referenced DLLs into Default even when Godot's
                // exported game owns them in an isolated context. Emit into the host's context so
                // native view/context types keep their identity in editor AND exported players.
                binary.Position = 0;
                var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(NativeScriptHost).Assembly)!;
                var generated = context.LoadFromStream(binary);
                var entryPoint = compilation.GetEntryPoint(CancellationToken.None)!;
                var submission = generated.GetType(entryPoint.ContainingType.MetadataName, throwOnError: true)!;
                var factory = submission.GetMethod(entryPoint.MetadataName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
                var states = new object[2];
                var run = (Task)factory.Invoke(null, [states])!;
                var timeout = request.TimeoutMs > 0 ? request.TimeoutMs : _policy.TimeoutMs;
                if (!run.Wait(timeout)) return ScriptEvaluationResult.Fail("timeout", $"脚本执行超过 {timeout}ms");
                var entry = submission.GetField(request.EntryName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (entry?.GetValue(states[1]) is not Delegate handler)
                    return ScriptEvaluationResult.Fail("entry-missing", $"脚本缺少委托入口 '{request.EntryName}'");
                if (ScriptHandlerAdapter.TryBind(request.ViewType, handler) is not Delegate bound)
                    return ScriptEvaluationResult.Fail("signature", $"入口 '{request.EntryName}' 签名不符；期望 {Describe(request.ViewType)}；实际 {Describe(handler.GetType())}");
                return ScriptEvaluationResult.Ok(bound);
            }
            catch (AggregateException e) { return ScriptEvaluationResult.Fail("runtime", e.InnerException?.Message ?? e.Message); }
            catch (Exception e) { return ScriptEvaluationResult.Fail("compile", e.Message); }
        }

        private static string Describe(Type type) => type + " @ " + System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly)?.Name
            + " [" + string.Join(";", type.GetGenericArguments().Select(t => t.FullName + " @ "
                + System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(t.Assembly)?.Name)) + "]";

        private static ScriptOptions Build(string? directory)
        {
            var references = new List<MetadataReference>();
            foreach (var name in CSharpScriptSecurityOptions.DefaultAllowedAssemblies.Append(typeof(DesktopTargetContext).Assembly.GetName().Name!))
            {
                var hostContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(NativeScriptHost).Assembly);
                var assembly = hostContext?.Assemblies.FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name)
                    ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name);
                // Framework facades may not be loaded yet. Engine assemblies must remain the host's instances.
                if (assembly is null && name.StartsWith("System.", StringComparison.Ordinal))
                {
                    try { assembly = Assembly.Load(name); }
                    catch (FileNotFoundException) { continue; } // Some native whitelist names are namespace-only on .NET.
                }
                if (assembly is null) throw new InvalidDataException($"缺少已加载的程序集 {name}");
                var path = assembly.Location;
                if (string.IsNullOrEmpty(path))
                {
                    if (directory is null) throw new InvalidDataException($"程序集 {name} 缺少宿主文件路径");
                    path = Path.Combine(directory, name + ".dll");
                    if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
                    if (!File.Exists(path) || AssemblyName.GetAssemblyName(path).FullName != assembly.GetName().FullName)
                        throw new InvalidDataException($"程序集 {name} 的编译文件与当前宿主不一致，请重新构建项目");
                }
                references.Add(MetadataReference.CreateFromFile(path));
            }
            return ScriptOptions.Default.WithReferences(references).WithImports(CSharpScriptSecurityOptions.DefaultAllowedImports);
        }
    }
}
