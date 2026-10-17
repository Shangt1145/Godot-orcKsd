using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyEditorScreen
{
    private string _scopePack="";
    private DiyWorkshopLibrary? _workshopLibrary;
    private Action? _workshopChanged;
    private GridContainer _keywordGrid=null!;
    private readonly Dictionary<string,SpinBox> _keywordValues=[];
    internal Button CreateNationButton=null!;
    internal OptionButton ArtInputKind=null!;
    private SpinBox _cropX=null!,_cropY=null!,_cropW=null!,_cropH=null!;
    private Button _cropApply=null!;
    private Action<string>? _receiveNationIcon;
    internal ConfirmationDialog? NationDialog;
    internal void OnNationIconChosen(string path)=>_receiveNationIcon?.Invoke(path);
    public void ConfigureWorkshop(DiyWorkshopLibrary library,Action changed){_workshopLibrary=library;_workshopChanged=changed;}
    internal void SetPackScope(DiyPackDraft pack)
    {
        _scopePack=pack.Id;
        Load(_library.Cards.FirstOrDefault(c=>c.PackId==pack.Id)??new(){PackId=pack.Id});
    }
    internal void GuardNavigation(Action action)=>Guard(action);
    internal void RefreshWorkshopOptions()
    {
        var draft=Read();_loading=true;RefreshWorkshopVocabulary(draft.PackId);Select(Nation,draft.Nation);
        foreach(var pair in _keywords)pair.Value.ButtonPressed=draft.Keywords.Contains(pair.Key);
        _loading=false;RefreshPreview();
    }
    private void RefreshWorkshopVocabulary(string packId)
    {
        Nation.Clear();foreach(var n in packId.Length>0?WorkshopAuthoring.NationsFor(packId):DiyCardCompiler.Nations)
        {Nation.AddItem(n.Label);Nation.SetItemMetadata(Nation.ItemCount-1,n.Id);}
        foreach(var value in _keywordValues.Values){value.GetParent().RemoveChild(value);value.QueueFree();}_keywordValues.Clear();
        foreach(var key in _keywords.Keys.Where(k=>k.StartsWith("kw-",StringComparison.Ordinal)).ToArray())
        {var control=_keywords[key];_keywordGrid.RemoveChild(control);control.QueueFree();_keywords.Remove(key);}
        foreach(var keyword in WorkshopAuthoring.KeywordsFor(packId))
        {
            var box=new CheckBox{Text=keyword.Label};_keywordGrid.AddChild(box);_keywords[keyword.Id]=box;box.Toggled+=_=>Changed();
            if(WorkshopAuthoring.Keyword(keyword.Id)?.ProgramJson is { } json)
            {
                var value=Number(0,10000);value.Value=_draft.KeywordValues.GetValueOrDefault(keyword.Id,WorkshopAuthoring.ParameterDefault(keyword.Id));
                value.TooltipText="词条参数 X";_keywordGrid.AddChild(value);_keywordValues[keyword.Id]=value;value.ValueChanged+=_=>Changed();
            }
        }
        if(ArtInputKind is not null)
        {
            ArtInputKind.Select(_draft.ArtSourceFile.Length==0?0:_draft.ArtImportMode=="card"?2:1);
            var r=_draft.ArtRegion;_cropX.Value=r.X*100;_cropY.Value=r.Y*100;_cropW.Value=r.Width*100;_cropH.Value=r.Height*100;
            _cropApply.Disabled=_draft.ArtSourceFile.Length==0;
        }
    }
    private void BuildNationFields(Node parent)
    {
        CreateNationButton=UiStyles.Button("创建 / 修改国家与图标…",OpenNationEditor);parent.AddChild(CreateNationButton);
    }
    private void OpenNationEditor()
    {
        if(_workshopLibrary is null||_scopePack.Length==0){_notice.Text="请先进入一个卡包或扩展包。";return;}
        var pack=_workshopLibrary.Packs.Single(p=>p.Id==_scopePack);
        var current=pack.Nations.FirstOrDefault(n=>n.Id==Value(Nation))??new DiyNationDraft();
        byte[]? pendingIcon=null;
        var dialog=new ConfirmationDialog{Title="国家与图标",OkButtonText="保存国家",MinSize=new(520,330)};NationDialog=dialog;AddChild(dialog);
        var form=new VBoxContainer();dialog.AddChild(form);
        var name=new LineEdit{Text=current.Name,MaxLength=40};Field(form,"国家名称",name);
        var color=new ColorPickerButton{Color=new Color(current.Color),CustomMinimumSize=new(0,40)};Field(form,"卡框国家色",color);
        var iconText=UiStyles.Label(current.IconFile.Length==0?"尚未导入国家图标":"已设置国家图标",13);form.AddChild(iconText);
        var preview=new TextureRect{CustomMinimumSize=new(64,64),ExpandMode=TextureRect.ExpandModeEnum.IgnoreSize,StretchMode=TextureRect.StretchModeEnum.KeepAspectCentered,Texture=DiyArtStore.Load(current.IconFile)};form.AddChild(preview);
        _receiveNationIcon=path=>
        {
                try
                {
                    var image=Image.LoadFromFile(path);if(image is null||image.GetWidth()>4096||image.GetHeight()>4096)throw new InvalidDataException("图标最大 4096×4096");
                    var scale=256f/Math.Max(image.GetWidth(),image.GetHeight());image.Resize(Math.Max(1,(int)(image.GetWidth()*scale)),Math.Max(1,(int)(image.GetHeight()*scale)),Image.Interpolation.Lanczos);
                    var canvas=Image.CreateEmpty(256,256,false,Image.Format.Rgba8);canvas.Fill(Colors.Transparent);image.Convert(Image.Format.Rgba8);canvas.BlitRect(image,new Rect2I(0,0,image.GetWidth(),image.GetHeight()),new Vector2I((256-image.GetWidth())/2,(256-image.GetHeight())/2));
                    pendingIcon=canvas.SavePngToBuffer();preview.Texture=ImageTexture.CreateFromImage(canvas);iconText.Text="图标已选中，保存国家后生效";
                }
                catch(Exception e){iconText.Text=e.Message;}
        };
        var choose=UiStyles.Button("导入国家图标…",()=>
        {
            var picker=new FileDialog{Access=FileDialog.AccessEnum.Filesystem,FileMode=FileDialog.FileModeEnum.OpenFile,UseNativeDialog=true,Filters=["*.png,*.jpg,*.jpeg,*.webp ; 图标图片"]};AddChild(picker);
            picker.FileSelected+=path=>{OnNationIconChosen(path);picker.QueueFree();};picker.Canceled+=()=>picker.QueueFree();picker.PopupCentered(new(700,480));
        });form.AddChild(choose);
        dialog.Confirmed+=()=>
        {
            try
            {
                var n=current with{Name=name.Text.Trim(),Color=color.Color.ToHtml(false)};
                if(pendingIcon is not null)n=n with{IconFile=_workshopLibrary.SaveNationIcon(n,pendingIcon)};
                _workshopLibrary.Save(pack with{Nations=pack.Nations.Where(nation=>nation.Id!=n.Id).Append(n).ToArray()});
                var draft=Read() with{Nation=n.Id};_workshopChanged?.Invoke();Load(draft);Changed();_notice.Text="国家与图标已保存。";
            }catch(Exception e){_notice.Text=e.Message;}_receiveNationIcon=null;NationDialog=null;dialog.QueueFree();
        };dialog.Canceled+=()=>{_receiveNationIcon=null;NationDialog=null;dialog.QueueFree();};dialog.PopupCentered(new(540,440));
    }
    private void BuildArtAdaptation(Node parent)
    {
        ArtInputKind=Option([new("auto","自动判断"),new("illustration","纯插画"),new("card","完整卡面：提取插画")]);Field(parent,"导入图片类型",ArtInputKind);
        ArtInputKind.ItemSelected+=_=>
        {
            if(_loading||_draft.ArtSourceFile.Length==0)return;
            var image=new Image();if(image.LoadPngFromBuffer(_library.ReadArt(_draft.ArtSourceFile))==Error.Ok)ImportAdaptedImage(image);
        };
        var row=new GridContainer{Columns=4};parent.AddChild(row);
        _cropX=Number(0,99);_cropY=Number(0,99);_cropW=Number(1,100);_cropH=Number(1,100);_cropW.Value=_cropH.Value=100;
        Field(row,"插画左边 %",_cropX);Field(row,"插画上边 %",_cropY);Field(row,"插画宽度 %",_cropW);Field(row,"插画高度 %",_cropH);
        _cropApply=UiStyles.Button("应用裁切预览",()=>
        {
            try
            {
                var image=new Image();if(image.LoadPngFromBuffer(_library.ReadArt(_draft.ArtSourceFile))!=Error.Ok)throw new IOException("原图无法读取");
                var region=new DiyArtRegion((float)_cropX.Value/100,(float)_cropY.Value/100,Math.Min((float)_cropW.Value/100,1-(float)_cropX.Value/100),Math.Min((float)_cropH.Value/100,1-(float)_cropY.Value/100));
                SaveAdapted(image,region,"card",_draft.ArtSourceFile);
            }catch(Exception e){_notice.Text=e.Message;}
        });_cropApply.Disabled=true;parent.AddChild(_cropApply);
        var hint=UiStyles.Label("完整卡面只提取插画，旧边框、旧数值与旧文字不再叠加。识别后可调整裁切；规则数值仍由上方字段编辑。",12,UiStyles.Dim);hint.AutowrapMode=TextServer.AutowrapMode.WordSmart;parent.AddChild(hint);
    }
    private void ImportAdaptedImage(Image image)
    {
        var mode=Value(ArtInputKind);if(mode=="auto")mode=image.GetHeight()>image.GetWidth()*1.2f?"card":"illustration";
        var original=_library.SaveArtSource(_draft.Id,image.SavePngToBuffer());
        SaveAdapted(image,mode=="card"?CardArtExtractor.Detect(image):new(0,0,1,1),mode,original);
    }
    private void SaveAdapted(Image image,DiyArtRegion region,string mode,string original)
    {
        var cropped=CardArtExtractor.Crop(image,region);var name=_library.SaveArt(_draft.Id,cropped.SavePngToBuffer());_textures.Invalidate("diyart://"+name);
        _importedArt=name;ArtZoom.Value=100;DiyArtStore.UpdateZoom(_draft.Id,100);
        _draft=_draft with{ArtCardId="",ArtSourceFile=original,ArtRegion=region,ArtImportMode=mode};
        _loading=true;_cropX.Value=region.X*100;_cropY.Value=region.Y*100;_cropW.Value=region.Width*100;_cropH.Value=region.Height*100;_cropApply.Disabled=false;_loading=false;
        Changed();RefreshPreview();_notice.Text=mode=="card"?"已提取插画并适配当前卡框、国家图标。可调整裁切预览；原图已保留。":"插画已导入，保存后用于构筑与对局。";
    }
}
