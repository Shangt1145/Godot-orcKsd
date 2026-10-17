using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using System.Text.Json.Nodes;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDesktopAuthoring()
    {
        var original=_diyEditor.CurrentDraft;
        var draft=new DiyCardDraft {Id="diy/godot-inspector-"+Guid.NewGuid().ToString("N"),Name="Godot 内置完整编辑器",Cost=0};
        try
        {
            _diyEditor.Load(draft);_diyEffects.Open(draft);Show("diyfx");
            var inspector=_diyEffects.NativeInspector;
            if(inspector.AdvancedVisible)throw new Exception("默认仍显示底层结构工具");
            async Task Click(Control control){inspector.EnsureFriendlyVisible(control);await DeckClickAsync(control);}
            void Choose(Control control,string id)
            {
                var pick=(OptionButton)control;
                for(var i=0;i<pick.ItemCount;i++)if(pick.GetItemMetadata(i).AsString()==id)
                {pick.Select(i);pick.EmitSignal(OptionButton.SignalName.ItemSelected,i);return;}
                throw new Exception("人话选项缺失："+id);
            }
            async Task AddAction(string path,string op)
            {
                await Click(inspector.FriendlyControls[path+"#add"]);
                if(inspector.PickerItems.ItemCount!=161)throw new Exception("易用界面裁剪了完整动作目录");
                inspector.PickFriendlyAction(op);await Click(inspector.PickerAddButton);
            }
            await Click(inspector.NewEffectButton);
            await AddAction("/effects/0/actions","loop");
            ((SpinBox)inspector.FriendlyControls["/effects/0/actions/0/times"]).Value=3;
            await Click(inspector.FriendlyControls["/effects/0/actions/0/times#calculation"]);
            Choose(inspector.FriendlyControls["/effects/0/actions/0/times#valueMode"],"math");
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            if(inspector.Document.At("/effects/0/actions/0/times/expr")!.ToString()!="math")throw new Exception("普通界面无法组合计算值");
            // Any numeric parameter can use the same expression UI; no card-specific templates.
            ((SpinBox)inspector.FriendlyControls["/effects/0/actions/0/times/items/0"]).Value=1;
            ((SpinBox)inspector.FriendlyControls["/effects/0/actions/0/times/items/1"]).Value=1;
            await AddAction("/effects/0/actions/0/actions","drawOne");
            await Click(inspector.UndoButton);
            if(inspector.Document.At("/effects/0/actions/0/actions")!.AsArray().Count!=0)throw new Exception("撤销失败");
            await Click(inspector.RedoButton);
            // A different composition exercises generic condition and action-list editors.
            await Click(inspector.NewEffectButton);
            await AddAction("/effects/1/actions","branch");
            Choose(inspector.FriendlyControls["/effects/1/actions/0/test#condition"],"compare");
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await AddAction("/effects/1/actions/0/then","drawOne");
            await Click(inspector.CardKeywordsButton);
            await Click(inspector.KeywordControls["blitz"]);
            await Click(inspector.KeywordDoneButton);
            if(inspector.Document.At("/kwMap/blitz")!.ToString()!="true")throw new Exception("普通词条选择没有写回");
            var before=inspector.Document.Root.ToJsonString();
            if(inspector.TryApplyRaw("{invalid"))throw new Exception("接受了无效 JSON");
            if(inspector.Document.Root.ToJsonString()!=before)throw new Exception("无效编辑破坏草稿");
            inspector.RawEditor.Text="{invalid";
            await DeckClickAsync(_diyEffects.SaveButton);
            if(new DiyCardLibrary(_diyPath).Cards.Any(c=>c.Id==draft.Id))throw new Exception("未完成的 JSON 仍报告保存");
            inspector.RawEditor.Text=inspector.Document.Root.ToJsonString(new(){WriteIndented=true});
            inspector.Document.Set("/name",JsonValue.Create("Godot 原生界面保存试验"));
            if(_diyEditor.CurrentDraft.Name!="Godot 原生界面保存试验")throw new Exception("卡面字段没有实时回写");
            await DeckClickAsync(_diyEffects.SaveButton);
            var stored=new DiyCardLibrary(_diyPath).Cards.Single(c=>c.Id==draft.Id);
            if(stored.OrcEffects.Length!=2||!stored.OrcEffects[0].DesktopJson!.Contains("drawOne"))throw new Exception("原生 UI 编辑未落盘");
            var native=Kards.Ui.OrcBridge.DiyCardCompiler.Validate(stored,_catalog.Cards);
            if(!native.Success)throw new Exception(string.Join("；",native.Errors));
            inspector.Document.Set("/future",JsonNode.Parse("{\"nested\":[null,false,{\"x\":7}]}"));
            inspector.SelectPath("/future/nested");
            await DeckClickAsync(_diyEffects.SaveButton);
            var reopened=new DiyCardLibrary(_diyPath).Cards.Single(c=>c.Id==draft.Id);
            if(!reopened.DesktopCardJson!.Contains("future"))throw new Exception("未知卡面字段丢失");
            // Opening every registered operation must preserve its declaration exactly.
            var basis=inspector.Document.Root.DeepClone().AsObject();
            var scoped=basis.DeepClone().AsObject();
            scoped["effects"]=JsonNode.Parse("[{\"trigger\":\"deploy\",\"targets\":[],\"actions\":[{\"op\":\"bindEvent\",\"target\":{\"sel\":\"self\"},\"effect\":{\"trigger\":\"death\",\"targets\":[],\"actions\":[{\"op\":\"dealDamage\",\"target\":{\"sel\":\"self\"},\"amount\":2}],\"futureNested\":{\"x\":[null,false]}}}]}]");
            inspector.Open(scoped);
            Choose(inspector.FriendlyControls["/effects/0/actions/0/effect/actions/0/target#target"],"pick");
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            if(inspector.Document.At("/effects/0/targets")!.AsArray().Count!=0||inspector.Document.At("/effects/0/actions/0/effect/targets")!.AsArray().Count!=1)throw new Exception("嵌套效果的目标串到外层");
            if(inspector.Document.At("/effects/0/actions/0/effect/futureNested/x/1")!.ToString()!="false")throw new Exception("易用目标选择丢失扩展字段");
            foreach(var op in Kards.Ui.OrcBridge.InspectorVocabulary.Operations)
            {
                var action=Kards.Ui.OrcBridge.InspectorVocabulary.Action(op);
                foreach(var field in Kards.Ui.OrcBridge.InspectorPresentation.Parameters(op))
                    action[field.GetProperty("name").GetString()!]=JsonNode.Parse(field.GetProperty("initial").GetRawText());
                action["futureField"]=JsonNode.Parse("{\"nested\":[null,false,7]}");
                var card=basis.DeepClone().AsObject();card["effects"]=new JsonArray(new JsonObject{["trigger"]="deploy",["actions"]=new JsonArray(action)});
                var originalJson=card.ToJsonString();inspector.Open(card);
                if(inspector.Document.Root.ToJsonString()!=originalJson)throw new Exception("易用表单改写了原声明："+op);
                await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            }
            inspector.Open(basis);
            inspector.Document.Remove("/future");await DeckClickAsync(_diyEffects.SaveButton);
            _diyEffects.Open(new DiyCardLibrary(_diyPath).Cards.Single(c=>c.Id==draft.Id));
            // Capture a simple visible value; the expression case above is checked separately.
            inspector.Document.Set("/effects/0/actions/0/times",JsonValue.Create(2));
            _diyEffects.Open(_diyEditor.Draft);
            if(OS.GetCmdlineUserArgs().Contains("--capture-desktop-authoring"))
            {
                foreach(var width in new[]{1600,1280})
                {
                    GetWindow().Size=new Vector2I(width,900);
                    await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                    await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                    var directory=ProjectSettings.GlobalizePath("res://artifacts/desktop-authoring");System.IO.Directory.CreateDirectory(directory);
                    GetViewport().GetTexture().GetImage().SavePng($"{directory}/godot-inspector-{width}.png");
                    if(inspector.Size.X>GetViewportRect().Size.X)throw new Exception("Inspector 横向溢出");
                }
            }
            GD.Print("DESKTOP_AUTHORING_UI_VERIFY_OK godot-native-controls operations=161 all-schema-forms-lossless friendly-picker nested-loop arbitrary-expression condition-branch field-edit mouse-add undo-redo invalid-json-keeps-draft native-compile save disk-reload unknown-fields no-browser no-node");
            GetTree().Quit();
        }
        catch(Exception e)
        {
            GD.PushError(e.ToString());
            if(OS.GetCmdlineUserArgs().Contains("--capture-desktop-authoring"))
            {await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath("res://artifacts/desktop-authoring/friendly-failure.png"));}
            GetTree().Quit(1);
        }
        finally{_diyEffects.Open(original);_diy.Delete(draft.Id);RefreshDiy();_diyEditor.Load(original);}
    }
}
