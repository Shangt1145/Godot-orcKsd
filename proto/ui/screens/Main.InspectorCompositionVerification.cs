using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using System.Text.Json.Nodes;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyInspectorComposition()
    {
        var original=_diyEditor.CurrentDraft;
        var draft=new DiyCardDraft{Id="diy/composition-probe-"+Guid.NewGuid().ToString("N"),Name="通用组合试作",Cost=0};
        var folder=ProjectSettings.GlobalizePath("res://artifacts/inspector-composition/"+GetWindow().Size.X);
        Directory.CreateDirectory(folder);
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        async Task Frame(){await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
        async Task Capture(string name){await Frame();await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);GetViewport().GetTexture().GetImage().SavePng(folder+"/"+name+".png");}
        void Choose(Control control,string value)
        {
            var pick=(OptionButton)control;
            for(var i=0;i<pick.ItemCount;i++)if(pick.GetItemMetadata(i).AsString()==value){pick.Select(i);pick.EmitSignal(OptionButton.SignalName.ItemSelected,i);return;}
            throw new InvalidOperationException("组合选项缺失："+value);
        }
        try
        {
            _diyEditor.Load(draft);_diyEffects.Open(draft);Show("diyfx");await MenuSettledAsync();
            var inspector=_diyEffects.NativeInspector;var composer=inspector.Composer;
            async Task Click(Control control){inspector.EnsureFriendlyVisible(control);await DeckClickAsync(control);}
            async Task Back(){await Click(composer.BackButton);}
            async Task Number(string path,string number)
            {
                Choose(composer.Controls[path+"#mode"],"literal");await Frame();
                var edit=(LineEdit)composer.Controls[path+"#number"];edit.Text=number;edit.EmitSignal(LineEdit.SignalName.TextChanged,number);
            }
            await Click(inspector.NewEffectButton);Choose(inspector.FriendlyControls["/effects/0/trigger"],"deploy");await Frame();
            await Click(inspector.FriendlyControls["/effects/0/actions#add"]);inspector.PickFriendlyAction("branch");await Click(inspector.PickerAddButton);
            const string target="/effects/0/actions/0/test";
            var before=inspector.Document.Root.ToJsonString();
            await Click(inspector.FriendlyControls[target+"#compose"]);
            Choose(composer.Controls["/value#mode"],"compare");await Frame();
            Check(composer.GetOkButton().Disabled,"未填写的判断不应允许应用");
            await Click(composer.Controls["/value/left#open"]);Choose(composer.Controls["/value/left#mode"],"count");await Frame();
            await Click(composer.Controls["/value/left/query#open"]);
            Choose(composer.Controls["/value/left/query/side"],"friendly");Choose(composer.Controls["/value/left/query/zone"],"hand");
            await Click(composer.Controls["/value/left/query/where#open"]);Choose(composer.Controls["/value/left/query/where#mode"],"compare");await Frame();
            await Click(composer.Controls["/value/left/query/where/left#open"]);Choose(composer.Controls["/value/left/query/where/left#mode"],"read");await Frame();
            Choose(composer.Controls["/value/left/query/where/left/of"],"candidate");await Frame();Choose(composer.Controls["/value/left/query/where/left/path"],"cost");
            await Back();Choose(composer.Controls["/value/left/query/where/cmp"],"<");
            await Click(composer.Controls["/value/left/query/where/right#open"]);await Number("/value/left/query/where/right","4");await Back();
            await Capture("02-candidate-filter");await Back();await Back();await Back();
            Choose(composer.Controls["/value/cmp"],">=");await Click(composer.Controls["/value/right#open"]);await Number("/value/right","0");await Back();
            Check(inspector.Document.Root.ToJsonString()==before,"应用前已修改源声明");
            Check(!composer.GetOkButton().Disabled,"完整判断不能应用");await Capture("03-condition-ready");
            await Click(composer.GetOkButton());var applied=inspector.Document.Root.ToJsonString();
            Check(inspector.Document.At(target+"/left/expr")!.ToString()=="count","组合没有写回真实声明");
            await Click(inspector.UndoButton);Check(inspector.Document.Root.ToJsonString()==before,"组合修改没有一次撤销");
            await Click(inspector.RedoButton);Check(inspector.Document.Root.ToJsonString()==applied,"组合重做失败");
            await Click(inspector.FriendlyControls[target+"#compose"]);Choose(composer.Controls["/value#mode"],"any");await Frame();
            await Click(composer.GetCancelButton());Check(inspector.Document.Root.ToJsonString()==applied,"取消破坏了原条件");
            await Click(inspector.FriendlyControls["/effects/0/actions/0/then#add"]);inspector.PickFriendlyAction("drawOne");await Click(inspector.PickerAddButton);
            var validation=DiyCardCompiler.Validate(_diyEditor.Draft,_catalog.Cards);Check(validation.Success,string.Join("；",validation.Errors));
            inspector.Document.Set("/future",JsonNode.Parse("{\"unknown\":[null,false,17]}"));
            await Click(_diyEffects.SaveButton);
            var stored=new DiyCardLibrary(_diyPath).Cards.Single(c=>c.Id==draft.Id);
            var unknownValidation=DiyCardCompiler.Validate(stored,_catalog.Cards);Check(!unknownValidation.Success,"未知字段绕过了准入");
            _diyEffects.Open(stored);await Frame();Check(inspector.Document.At("/future/unknown/2")!.ToString()=="17","重开丢失未知字段");
            Check(inspector.Document.At(target+"/left/query/where/left/of")!.ToString()=="candidate","候选对象引用丢失");
            inspector.Document.Remove("/future");await Click(_diyEffects.SaveButton);
            _diyEffects.Open(new DiyCardLibrary(_diyPath).Cards.Single(c=>c.Id==draft.Id));await Frame();
            await Capture("01-overview");
            Check(inspector.GetGlobalRect().End.X<=GetViewportRect().Size.X+2,"编辑器超出窗口");
            GD.Print("INSPECTOR_COMPOSITION_VERIFY_OK query-count candidate-property compare generic-branch cancel atomic-undo redo native-compile save-reopen unknown-fields size="+GetWindow().Size);
        }
        catch(Exception error){GD.PushError(error.ToString());await Capture("failure");GetTree().Quit(1);return;}
        finally{_diy.Delete(draft.Id);RefreshDiy();_diyEditor.Load(original);_diyEffects.Open(original);}
        GetTree().Quit();
    }
}
