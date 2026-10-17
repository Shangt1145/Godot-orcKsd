using System.Reflection;
using System.Runtime.Loader;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

public class NativeScriptHostIsolationTests
{
    private sealed class GameContext(string directory) : AssemblyLoadContext("export-regression", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null || name.Name.StartsWith("System.", StringComparison.Ordinal)) return null;
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    [Fact]
    public void ReviewedEffectsBindToTheGamesIsolatedEngineTypes()
    {
        // Default already owns Orc through the test host. The exported game loads its own copy;
        // Roslyn delegates must bind to that copy, never to Default's equally named types.
        var directory = Path.GetDirectoryName(typeof(NativeScriptHost).Assembly.Location)!;
        var context = new GameContext(directory);
        var bridge = context.LoadFromAssemblyPath(Path.Combine(directory, "Kards.Ui.OrcBridge.dll"));
        var core = context.LoadFromAssemblyPath(Path.Combine(directory, "Kards.Ui.Core.dll"));
        bridge.GetType("Kards.Ui.OrcBridge.NativeScriptHost")!.GetMethod("ConfigureAssemblyDirectory")!.Invoke(null, [directory]);
        bridge.GetType("Kards.Ui.OrcBridge.OrcEngineHost")!.GetMethod("WarmUp")!.Invoke(null, [null]);
        var catalogType = core.GetType("Kards.Ui.Core.CardCatalog")!;
        var catalog = Activator.CreateInstance(catalogType)!;
        const string source = @"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
        catalogType.GetMethod("Load")!.Invoke(catalog, [source, null]);
        var cards = catalogType.GetProperty("Cards")!.GetValue(catalog)!;
        var serviceType = bridge.GetType("Kards.Ui.OrcBridge.DeckConstructionService")!;
        var service = Activator.CreateInstance(serviceType, cards, source)!;
        var report = serviceType.GetProperty("Report")!.GetValue(service)!;
        Assert.Equal(24, (int)report.GetType().GetProperty("Verified")!.GetValue(report)!);
    }
}
