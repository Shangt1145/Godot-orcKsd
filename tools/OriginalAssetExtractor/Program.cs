using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Assets.Utils;
using CUE4Parse.UE4.Readers;

var input=args[0];var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
using var provider=new DefaultFileProvider(input,SearchOption.AllDirectories,new VersionContainer(EGame.GAME_UE5_6));
provider.Initialize();provider.Mount();
var mapping=Path.Combine(output,"Kards-legacy.usmap");
if(File.Exists(mapping))provider.MappingsContainer=new FileUsmapTypeMappingsProvider(mapping);
Console.WriteLine($"PAK_MOUNTS mounted={provider.MountedVfs.Count} locked={provider.UnloadedVfs.Count} required={provider.RequiredKeys.Count}");
if(args.Length>2)
    foreach(var key in File.ReadAllLines(args[2]))
    {
        foreach(var guid in provider.RequiredKeys.ToArray())
            try{provider.SubmitKey(guid,new FAesKey(key));}catch{}
        if(provider.Files.Count>0)break;
    }
var paths=provider.Files.Keys.Order(StringComparer.Ordinal).ToArray();
provider.PostMount();
provider.LoadLocalization("zh-Hans");
File.WriteAllLines(Path.Combine(output,"files.txt"),paths);
Console.WriteLine($"PAK_FILES {paths.Length}");

if(args.Contains("--metadata-only"))
{
    var directory=Path.Combine(output,"cosmetic-data");Directory.CreateDirectory(directory);
    foreach(var path in paths.Where(p=>p.EndsWith(".uasset")&&(p.Contains("/Data/BoardItems/")||p.Contains("/BoardActorsSystem/BP_")||p.Contains("emojiAnimationWidget")||p.Contains("W_Battle_HUD_EmojiMenu"))))
    try
    {
        File.WriteAllText(Path.Combine(directory,Path.GetFileNameWithoutExtension(path)+".json"),Newtonsoft.Json.JsonConvert.SerializeObject(provider.LoadPackage(path).GetExports(),Newtonsoft.Json.Formatting.Indented));
    }
    catch(Exception error){Console.WriteLine("METADATA_ERROR "+path+" "+error.Message);}
    Console.WriteLine("COSMETIC_METADATA "+Directory.GetFiles(directory).Length);return;
}
var texturePaths=paths.Where(p=>p.EndsWith(".uasset") && (p.Contains("/BoardProps/") && Path.GetFileName(p).StartsWith("T_") || p.Contains("/Textures/emoji/") || p.Contains("/Textures/Icons/")&&System.Text.RegularExpressions.Regex.IsMatch(p,"Emote|Bubble|[Hh]ealth|[Ss]hield") || p.Contains("/Textures/Images/HQs/") || p.Contains("/NUI/Icons/") || p.Contains("/NUI/CustomizeBattlefields/") || p.Contains("/NUI/ButtonTextures/") || p.Contains("/NUI/MasterBorder/") || p.Contains("T_customization-selection-border") || p.Contains("/Icons/IconDefenceBase."))).ToArray();
var exported=new List<object>();

foreach(var testPath in texturePaths)
try
{
    var raw=provider.SaveAsset(Path.ChangeExtension(testPath,"uexp"));

    var package=provider.LoadPackage(testPath);

    foreach(var obj in package.GetExports())
    {
        if(obj is UTexture2D texture)
        {
            var image=texture.Decode();
            if(image is null)
            {
                var marker=raw.AsSpan().IndexOf("PF_"u8);
                if(marker>=32)
                {
                    var summary=((Package)package).Summary;
                    using var archive=new FAssetArchive(new FByteArchive(testPath,raw,provider.Versions),package,summary.TotalHeaderSize);
                    var bulkPath=Path.ChangeExtension(testPath,"ubulk");
                    if(provider.Files.ContainsKey(bulkPath))archive.AddPayload(PayloadType.UBULK,new FAssetArchive(new FByteArchive(bulkPath,provider.SaveAsset(bulkPath),provider.Versions),package,(int)summary.BulkDataStartOffset));
                    archive.Position=marker-32;
                    var platform=new FTexturePlatformData(archive,texture,true);
                    typeof(UTexture).GetProperty(nameof(UTexture.PlatformData))!.SetValue(texture,platform);
                    typeof(UTexture).GetProperty(nameof(UTexture.Format))!.SetValue(texture,Enum.Parse<EPixelFormat>(platform.PixelFormat));
                    image=texture.Decode();
                }
            }
            Console.WriteLine($"DECODE {texture.Name} {texture.PlatformData.SizeX}x{texture.PlatformData.SizeY} {(image is null?"EMPTY":image.PixelFormat.ToString())}");
            if(image is null)continue;
            using var bitmap=new SKBitmap(new SKImageInfo(image.Width,image.Height,image.PixelFormat==EPixelFormat.PF_R8G8B8A8?SKColorType.Rgba8888:SKColorType.Bgra8888,SKAlphaType.Unpremul));
            System.Runtime.InteropServices.Marshal.Copy(image.Data,0,bitmap.GetPixels(),image.Data.Length);
            using var encoded=bitmap.Encode(SKEncodedImageFormat.Png,100);
                        var destination=Path.Combine(output,"textures",Path.ChangeExtension(testPath,"png"));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var png=encoded.ToArray();File.WriteAllBytes(destination,png);
            exported.Add(new{Asset=testPath,File=destination,Width=image.Width,Height=image.Height,Sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png))});
        }
    }
}
catch(Exception error){Console.WriteLine("DECODE_ERROR "+testPath+" "+error.Message);}

File.WriteAllText(Path.Combine(output,"textures.json"),System.Text.Json.JsonSerializer.Serialize(exported,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
foreach(var dataPath in new[]{"kards/Content/Structs/BoardItems2.uasset","kards/Content/Structs/BackgroundTypeDataTable.uasset","kards/Content/Structs/emojiList.uasset","kards/Content/Structs/boardMats.uasset","kards/Content/Structs/TextStyles/emoteInBattleTextStyles.uasset","kards/Content/Structs/TextStyles/customizationEmoteDropdownStyles.uasset","kards/Content/Structs/TextStyles/customizationEmoteTextImages.uasset"})
try
{
    var objects=provider.LoadPackage(dataPath).GetExports();
    File.WriteAllText(Path.Combine(output,Path.GetFileNameWithoutExtension(dataPath)+".json"),Newtonsoft.Json.JsonConvert.SerializeObject(objects,Newtonsoft.Json.Formatting.Indented));
    Console.WriteLine("DATA_EXPORTED "+dataPath);
}
catch(Exception error){Console.WriteLine("DATA_ERROR "+dataPath+" "+error.Message);}
foreach(var fontPath in paths.Where(p=>p.EndsWith(".ufont")))
{
    var font=provider.SaveAsset(fontPath);var destination=Path.Combine(output,"fonts",Path.GetFileNameWithoutExtension(fontPath)+".ttf");Directory.CreateDirectory(Path.GetDirectoryName(destination)!);var fontLength=BitConverter.ToInt32(font);if(fontLength<12 || fontLength>font.Length-4)throw new InvalidDataException("Unexpected cooked font length: "+fontPath);File.WriteAllBytes(destination,font.AsSpan(4,fontLength).ToArray());
}
Console.WriteLine("TEXTURES_EXPORTED "+exported.Count);

var session=new CUE4Parse_Conversion.ExportSession { MaxDegreeOfParallelism=2 };
foreach(var meshPath in paths.Where(p=>p.EndsWith(".uasset") && p.Contains("/BoardProps/") && Path.GetFileName(p).StartsWith("SM_")))
try
{
    var package=(Package)provider.LoadPackage(meshPath);
    foreach(var obj in package.ExportsLazy.Select(e=>e.Value))
    if(obj is CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh mesh)
    {
        if(mesh.RenderData is null)
        {
            var entry=package.ExportMap.Single(e=>e.ObjectName.Text==mesh.Name);
            var raw=provider.SaveAsset(Path.ChangeExtension(meshPath,"uexp"));
            var start=(int)(entry.SerialOffset-package.Summary.TotalHeaderSize);
            var end=Math.Min(start+1024,start+(int)entry.SerialSize-38);
            for(var at=start;at<end;at++)
            {
                if((raw[at]!=1&&raw[at]!=5)||raw[at+1]!=0||BitConverter.ToInt32(raw,at+2)!=1)continue;
                var body=BitConverter.ToInt32(raw,at+6);var nav=BitConverter.ToInt32(raw,at+10);
                var sockets=BitConverter.ToInt32(raw,at+30);
                if(body<0||body>package.ExportMapLength||nav<0||nav>package.ExportMapLength||sockets!=0)continue;
                var lodCount=BitConverter.ToInt32(raw,at+34);if(lodCount<1||lodCount>8)continue;
                try
                {
                    using var archive=new FAssetArchive(new FByteArchive(meshPath,raw,provider.Versions),package,package.Summary.TotalHeaderSize);
                    var bulkPath=Path.ChangeExtension(meshPath,"ubulk");
                    if(provider.Files.ContainsKey(bulkPath))archive.AddPayload(PayloadType.UBULK,new FAssetArchive(new FByteArchive(bulkPath,provider.SaveAsset(bulkPath),provider.Versions),package,(int)package.Summary.BulkDataStartOffset));
                    archive.Position=at+34;
                    var render=new CUE4Parse.UE4.Assets.Exports.StaticMesh.FStaticMeshRenderData(archive);
                    archive.ReadBoolean();
                    var materials=archive.ReadArray(()=>new CUE4Parse.UE4.Assets.Exports.StaticMesh.FStaticMaterial(archive));
                    typeof(CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh).GetProperty("RenderData")!.SetValue(mesh,render);
                    typeof(CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh).GetProperty("StaticMaterials")!.SetValue(mesh,materials);
                    typeof(CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh).GetProperty("Materials")!.SetValue(mesh,materials.Select(m=>(CUE4Parse.UE4.Objects.UObject.FPackageIndex?)m.MaterialInterface).ToArray());
                    break;
                }
                catch(Exception error){Console.WriteLine("MESH_NATIVE_ERROR "+mesh.Name+" "+error.Message);}
            }
        }
        session.Add(mesh);
    }
}
catch(Exception error){Console.WriteLine("MESH_LOAD_ERROR "+meshPath+" "+error.Message);}
foreach(var result in await session.RunAsync(Path.Combine(output,"models"),new CUE4Parse_Conversion.Options.ExportOptions(meshFormat:CUE4Parse_Conversion.Options.EMeshFormat.Gltf2)))Console.WriteLine($"MESH_EXPORT {result.Success} {result.ObjectPath} {result.Error?.Message}");
