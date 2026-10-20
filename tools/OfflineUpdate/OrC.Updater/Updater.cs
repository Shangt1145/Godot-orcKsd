using Godot;
using OrC.Update;
using System.Collections.Concurrent;
using System.Diagnostics;

public partial class Updater : Control
{
    private readonly ConcurrentQueue<Action> _dispatch=new();
    private string _install="",_patch="";
    private Label _folder=null!,_package=null!,_details=null!,_status=null!;
    private Button _chooseFolder=null!,_choosePatch=null!,_apply=null!,_cancel=null!,_start=null!,_rollback=null!;
    private ProgressBar _progress=null!;
    private CancellationTokenSource? _cancelUpdate;
    private bool _busy,_closing;
    private string? _verifyExpected;
    public override async void _Ready()
    {
        GetTree().AutoAcceptQuit=false;GetWindow().CloseRequested+=()=>{if(_busy){_closing=true;_cancelUpdate?.Cancel();}else GetTree().Quit();};
        var args=OS.GetCmdlineUserArgs();
        if(args.Length>=4&&args[0]=="--verify-update")
        {
            try
            {
                if(!OS.HasFeature("editor"))Installer.InstallUpdater(OS.GetExecutablePath().GetBaseDir(),args[1],true);
                await Task.Run(()=>Installer.ApplyAsync(args[1],args[2]));
                if(Builds.Scan(args[1],"verify").BuildId!=Builds.Scan(args[3],"verify").BuildId)throw new Exception("更新结果不一致。");
                GD.Print("GODOT_UPDATER_VERIFY_OK exact-files native-offline-ui-core");GetTree().Quit();
            }
            catch(Exception e){GD.PushError(e.ToString());GetTree().Quit(1);}return;
        }
        Theme=new Theme{DefaultFont=new SystemFont{FontNames=["Microsoft YaHei UI","Microsoft YaHei","Noto Sans CJK SC"]},DefaultFontSize=17};
        var margin=new MarginContainer();AddChild(margin);margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach(var edge in new[]{"left","right","top","bottom"})margin.AddThemeConstantOverride("margin_"+edge,30);
        var column=new VBoxContainer();column.AddThemeConstantOverride("separation",16);margin.AddChild(column);
        var title=Text(column,"OrC-KSD  离线更新 1.1",30);title.Modulate=new Color("d5c28c");
        Text(column,"选择补丁，点击更新。卡组、DIY 卡和导入卡图会保留。",16);
        _folder=Text(column,"",16);_chooseFolder=Button(column,"选择游戏文件夹…",()=>Choose(true));
        _package=Text(column,"尚未选择补丁",16);_choosePatch=Button(column,"选择离线补丁…",()=>Choose(false));
        _details=Text(column,"首次更新：请选择包含 KardsUi.exe 的文件夹。",16);_details.CustomMinimumSize=new(0,62);
        _progress=new ProgressBar{ShowPercentage=true,MaxValue=100};column.AddChild(_progress);
        _status=Text(column,"准备就绪。",16);_status.CustomMinimumSize=new(0,54);
        var row=new HBoxContainer();row.AddThemeConstantOverride("separation",12);column.AddChild(row);
        _apply=Button(row,"更新",()=>Run(false));_cancel=Button(row,"取消",()=>_cancelUpdate?.Cancel());
        _start=Button(row,"启动游戏",StartGame);_rollback=Button(row,"回退上一版",()=>Run(true));
        _apply.CustomMinimumSize=new(120,44);_cancel.Disabled=true;
        _install=OS.GetExecutablePath().GetBaseDir();
        if(args.Length>=2&&args[0]=="--install")_install=args[1];
        Refresh();await RecoverIfNeeded();
        if(args.Length>=4&&args[0]=="--verify-ui-update")
        {
            _install=args[1];_verifyExpected=args[3];LoadPatch(args[2]);Refresh();
            if(_apply.Disabled){GD.PushError("更新按钮未启用："+_status.Text);GetTree().Quit(1);return;}
            _apply.EmitSignal(BaseButton.SignalName.Pressed);
        }
        if(args.Length>=2&&args[0]=="--capture")
        {
            await ToSignal(GetTree().CreateTimer(.5),SceneTreeTimer.SignalName.Timeout);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(args[1]);GetTree().Quit();
        }
    }
    private static Label Text(Node parent,string text,int size)
    {var label=new Label{Text=text,AutowrapMode=TextServer.AutowrapMode.WordSmart};label.AddThemeFontSizeOverride("font_size",size);parent.AddChild(label);return label;}
    private static Button Button(Node parent,string text,Action action)
    {var button=new Button{Text=text,CustomMinimumSize=new(0,38)};button.Pressed+=action;parent.AddChild(button);return button;}
    private void Refresh()
    {
        _folder.Text="游戏位置："+_install;_package.Text=string.IsNullOrEmpty(_patch)?"尚未选择补丁":"补丁："+System.IO.Path.GetFileName(_patch);
        var valid=System.IO.File.Exists(System.IO.Path.Combine(_install,"KardsUi.exe"));
        _apply.Disabled=_busy||!valid||string.IsNullOrEmpty(_patch);_chooseFolder.Disabled=_choosePatch.Disabled=_busy;
        try{_start.Disabled=_busy||!valid||Installer.HasRecovery(_install);_rollback.Disabled=_busy||!Installer.CanRollback(_install);}
        catch(Exception e){_apply.Disabled=_start.Disabled=_rollback.Disabled=true;_status.Text=e.Message;}
    }
    private void Choose(bool folder)
    {
        var dialog=new FileDialog{Access=FileDialog.AccessEnum.Filesystem,FileMode=folder?FileDialog.FileModeEnum.OpenDir:FileDialog.FileModeEnum.OpenFile,Title=folder?"选择游戏文件夹":"选择离线补丁",UseNativeDialog=true};
        if(!folder)dialog.Filters=["*.orcupdate ; OrC-KSD 离线补丁"];
        AddChild(dialog);
        dialog.DirSelected+=async path=>{_install=path;Refresh();dialog.QueueFree();await RecoverIfNeeded();};
        dialog.FileSelected+=path=>{LoadPatch(path);dialog.QueueFree();};
        dialog.Canceled+=()=>dialog.QueueFree();dialog.PopupCentered(new Vector2I(760,480));
    }
    private void LoadPatch(string path)
    {
            try
            {
                var variants=PatchBuilder.InspectAll(path);var p=variants[0];_patch=path;
                _details.Text=variants.Length==1
                    ?$"{p.Source.Version} → {p.Target.Version}\n构建 {p.Source.BuildId[..12]} → {p.Target.BuildId[..12]} · 补丁 {new System.IO.FileInfo(path).Length/1048576d:F1} MB"
                    :$"兼容更新 · 自动识别 {variants.Length} 个来源构建 → {p.Target.Version}（{p.Target.BuildId[..12]}）\n支持：{string.Join("、",variants.Select(v=>v.Source.BuildId[..12]))} · 补丁 {new System.IO.FileInfo(path).Length/1048576d:F1} MB";
                _status.Text="会按本地实际文件自动匹配来源；请先保存进度并关闭游戏。";
            }
            catch(Exception e){_patch="";_status.Text=e.Message;}
            Refresh();
    }
    private async Task RecoverIfNeeded()
    {
        if(!Installer.HasRecovery(_install))return;
        _busy=true;_cancelUpdate=new();Refresh();
        try
        {
            _status.Text="发现未完成的更新。请关闭游戏，正在恢复…";
            while(Installer.GameIsRunning(_install))await Task.Delay(500,_cancelUpdate.Token);
            await Task.Run(()=>Installer.Recover(_install));_status.Text="已恢复，可以重新选择补丁。";
        }
        catch(Exception e){_status.Text="恢复未完成："+e.Message;}
        finally{_busy=false;_cancelUpdate.Dispose();_cancelUpdate=null;Refresh();if(_closing)GetTree().Quit();}
    }
    private async void Run(bool rollback)
    {
        if(_busy)return;_busy=true;_cancelUpdate=new();_cancel.Disabled=rollback;Refresh();
        var success=false;
        var progress=new Progress<UpdateProgress>(p=>_dispatch.Enqueue(()=>
        {if(!_busy)return;_status.Text=p.Message;_progress.Value=p.Fraction*100;_cancel.Disabled=p.Fraction>=.8;}));
        try
        {
            if(rollback){await Task.Run(()=>Installer.Rollback(_install));_status.Text="已回退到上一版，可以启动游戏。";_progress.Value=100;}
            else
            {
                await Task.Run(async()=>
                {
                    if(!OS.HasFeature("editor"))Installer.InstallUpdater(OS.GetExecutablePath().GetBaseDir(),_install,true);
                    await Installer.ApplyAsync(_install,_patch,progress:progress,ct:_cancelUpdate.Token);
                });
                _status.Text="更新完成，可以启动游戏。请保留此新版更新器，今后继续用它更新。";_progress.Value=100;
            }
            success=true;
        }
        catch(OperationCanceledException){_status.Text="更新已取消，原游戏已保留。";}
        catch(Exception e){_status.Text="更新未完成："+e.Message;}
        finally
        {
            _busy=false;_cancel.Disabled=true;_cancelUpdate.Dispose();_cancelUpdate=null;Refresh();
            if(_verifyExpected is not null)
            {
                try
                {
                    if(!success||_start.Disabled||_rollback.Disabled||_progress.Value!=100||Builds.Scan(_install,"verify").BuildId!=Builds.Scan(_verifyExpected,"verify").BuildId)throw new Exception("原生更新界面未完成升级："+_status.Text);
                    GD.Print("GODOT_UPDATER_VERIFY_OK exact-files native-offline-ui-core choose-patch update-button start-ready rollback-ready");GetTree().Quit();
                }
                catch(Exception e){GD.PushError(e.ToString());GetTree().Quit(1);}
            }
            else if(_closing)GetTree().Quit();
        }
    }
    private void StartGame()
    {
        try{Process.Start(new ProcessStartInfo(System.IO.Path.Combine(_install,"KardsUi.exe")){WorkingDirectory=_install,UseShellExecute=true});GetTree().Quit();}
        catch(Exception e){_status.Text=e.Message;}
    }
    public override void _Process(double delta){while(_dispatch.TryDequeue(out var action))action();}
}
