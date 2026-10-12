using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
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
        private readonly Lazy<(ScriptOptions Options, InteractiveAssemblyLoader Loader)> _runtime = new(() => Build(directory));

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
                var runtime = _runtime.Value;
                var script = CSharpScript.Create(request.Source, runtime.Options, assemblyLoader: runtime.Loader);
                var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                if (errors.Length > 0)
                    return ScriptEvaluationResult.Fail("compile", string.Join("；", errors.Select(e => e.GetMessage())));
                var run = script.RunAsync();
                var timeout = request.TimeoutMs > 0 ? request.TimeoutMs : _policy.TimeoutMs;
                if (!run.Wait(timeout)) return ScriptEvaluationResult.Fail("timeout", $"脚本执行超过 {timeout}ms");
                var entry = run.Result.Variables.FirstOrDefault(v => v.Name == request.EntryName);
                if (entry?.Value is not Delegate handler)
                    return ScriptEvaluationResult.Fail("entry-missing", $"脚本缺少委托入口 '{request.EntryName}'");
                if (ScriptHandlerAdapter.TryBind(request.ViewType, handler) is not Delegate bound)
                    return ScriptEvaluationResult.Fail("signature", $"入口 '{request.EntryName}' 签名不符");
                return ScriptEvaluationResult.Ok(bound);
            }
            catch (AggregateException e) { return ScriptEvaluationResult.Fail("runtime", e.InnerException?.Message ?? e.Message); }
            catch (Exception e) { return ScriptEvaluationResult.Fail("compile", e.Message); }
        }

        private static (ScriptOptions, InteractiveAssemblyLoader) Build(string? directory)
        {
            var references = new List<MetadataReference>();
            var loader = new InteractiveAssemblyLoader();
            foreach (var name in CSharpScriptSecurityOptions.DefaultAllowedAssemblies)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name);
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
                // Roslyn's generated delegate must bind to Godot's already loaded native types.
                loader.RegisterDependency(assembly);
            }
            return (ScriptOptions.Default.WithReferences(references).WithImports(CSharpScriptSecurityOptions.DefaultAllowedImports), loader);
        }
    }
}
