using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    {
        ScriptReferenceSource.ConfigureDirectory(directory);
        Volatile.Write(ref _evaluator, new HostEvaluator(Path.GetFullPath(directory)));
    }

    internal static IScriptEvaluator Evaluator => Volatile.Read(ref _evaluator);

    /// <summary>
    /// 无物理 DLL 场景（安卓 APK／单文件发布）的程序集引用来源。
    /// <para>
    /// **为什么本仓库自带这一份**：这段能力原本在引擎的 <c>Orc.Script.ScriptAssemblyReferences</c> 里，
    /// v4 把它删了——引擎自己的求值器现在遇到无路径程序集**直接跳过**（见 <c>CSharpScriptEvaluator</c>
    /// 的 "单文件发布等无路径场景：跳过"）。跳过在桌面无感，但在安卓上会让所有引用本程序集的 csx 编译失败。
    /// 引擎侧没有替代入口，所以这段必须留在宿主侧（不修改引擎）。
    /// </para>
    /// <para>
    /// 三级回退：①程序集真实路径 → ②宿主提供的同目录副本 → ③读取已加载程序集的元数据（唯一能覆盖
    /// 纯内存加载的路径，需要 <c>unsafe</c>；<c>TryGetRawMetadata</c> 只有指针重载）。
    /// </para>
    /// </summary>
    private static class ScriptReferenceSource
    {
        private static string? _directory;

        /// <summary>宿主自带的同版本运行时程序集副本目录（Mono 拿不到原始元数据时用）。</summary>
        public static void ConfigureDirectory(string directory) => Volatile.Write(ref _directory, Path.GetFullPath(directory));

        public static unsafe MetadataReference FromAssembly(Assembly assembly)
        {
            if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
                return MetadataReference.CreateFromFile(assembly.Location);

            var directory = Volatile.Read(ref _directory);
            var copy = directory is null ? null : Path.Combine(directory, assembly.GetName().Name + ".dll");
            if (copy is not null && File.Exists(copy))
                return MetadataReference.CreateFromFile(copy);

            if (assembly.TryGetRawMetadata(out var blob, out var length))
                return AssemblyMetadata
                    .Create(ModuleMetadata.CreateFromMetadata((IntPtr)blob, length))
                    .GetReference(filePath: assembly.GetName().Name + ".dll");

            throw new InvalidDataException("无法取得脚本编译引用：" + assembly.FullName);
        }
    }

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
                // Scripting's convenience API unconditionally reopens object.Assembly.Location,
                // which is a relative/nonexistent path in Android Mono. Supply all metadata directly.
                var options = _runtime.Value;
                var tree = CSharpSyntaxTree.ParseText(request.Source, new CSharpParseOptions(kind: SourceCodeKind.Script));
                var compilation = CSharpCompilation.CreateScriptCompilation("OrcEffect_" + Guid.NewGuid().ToString("N"), tree,
                    references: options.MetadataReferences,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, scriptClassName: "Submission",
                        usings: options.Imports, allowUnsafe: false), returnType: typeof(object));
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
            // dynamic（承载视图的唯一形态）需要 DLR 绑定器程序集：它未必已被进程加载，不显式拉一次，
            // 引用集里就没有它，csx 里写 dynamic 会直接编译失败。引擎自己的求值器同样先显式拉这两个。
            foreach (var required in new[] { "Microsoft.CSharp", "System.Linq.Expressions" })
            {
                try { _ = Assembly.Load(required); }
                catch (Exception) { /* 拉不起来＝引用集里没有它；后面的编译会明确报错，不静默降级 */ }
            }

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
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    references.Add(ScriptReferenceSource.FromAssembly(assembly));
                    continue;
                }
                references.Add(MetadataReference.CreateFromFile(path));
            }
            return ScriptOptions.Default.WithReferences(references).WithImports(CSharpScriptSecurityOptions.DefaultAllowedImports);
        }
    }
}
