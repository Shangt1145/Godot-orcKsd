using Godot;
using System.Diagnostics;

namespace Kards.Ui;

public partial class Main
{
    private bool ResumeInterruptedUpdate()
    {
        if(MobilePlatform.IsAndroid)return false;
        if(OS.HasFeature("editor"))return false;
        var root=OS.GetExecutablePath().GetBaseDir();
        if(!System.IO.Directory.Exists(System.IO.Path.Combine(root,".orcksd-update/pending")))return false;
        StartUpdater(root);return true;
    }
    private void OpenOfflineUpdater()
    {
        if (MobilePlatform.IsAndroid)
        {
            var picker = new FileDialog { Title = "安装新版 APK（保留卡组和工坊）", Access = FileDialog.AccessEnum.Filesystem,
                FileMode = FileDialog.FileModeEnum.OpenFile, UseNativeDialog = true, Filters = ["*.apk ; Android 安装包"] };
            AddChild(picker); picker.FileSelected += path => {
                try { MobilePlatform.InstallApk(path); }
                catch(Exception error) { var message = new AcceptDialog { Title = "无法开始安装", DialogText = error.Message }; AddChild(message); message.Confirmed += message.QueueFree; message.PopupCentered(); }
                finally { picker.QueueFree(); }
            };
            picker.Canceled += picker.QueueFree; picker.PopupCentered(); return;
        }
        var confirm=new ConfirmationDialog{Title="离线更新",DialogText="更新需要退出游戏。请先结束当前对局；更新器会保留卡组、DIY 卡和导入卡图。",OkButtonText="退出并打开更新器",CancelButtonText="返回游戏"};
        AddChild(confirm);confirm.Confirmed+=()=>{confirm.QueueFree();StartUpdater(OS.GetExecutablePath().GetBaseDir());};
        confirm.Canceled+=()=>confirm.QueueFree();confirm.PopupCentered();
    }
    private void StartUpdater(string root)
    {
        try
        {
            var process=new ProcessStartInfo(System.IO.Path.Combine(root,"更新游戏.exe")){WorkingDirectory=root,UseShellExecute=false};
            process.ArgumentList.Add("--");process.ArgumentList.Add("--install");process.ArgumentList.Add(root);
            Process.Start(process);GetTree().Quit();
        }
        catch(Exception e)
        {
            var dialog=new AcceptDialog{Title="无法打开更新器",DialogText="请先解压离线更新器，再选择此游戏文件夹。\n"+e.Message};
            AddChild(dialog);dialog.Confirmed+=()=>dialog.QueueFree();dialog.PopupCentered();
        }
    }
}
