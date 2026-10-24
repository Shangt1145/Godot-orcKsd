using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Core;
using Orc.Protocol;

namespace Kards.Ui.OrcBridge;

public sealed partial class RemoteMatchFeed
{
    public bool SupportsCosmetics { get; private init; }
    private CosmeticLibrary? _cosmetics;
    private string? _cosmeticDeckId, _cosmeticNation;
    public CosmeticProfile LocalCosmetics => _cosmetics?.Resolve(_cosmeticDeckId,_cosmeticNation) ?? new();
    private readonly SemaphoreSlim _cosmeticPublish = new(1,1);
    private readonly object _cosmeticLock = new();
    private readonly Dictionary<int,CosmeticProfile> _cosmeticProfiles = new();
    private readonly Dictionary<int,int> _cosmeticLoads = new();
    private readonly Dictionary<int,long> _cosmeticVersions = new();
    private string _cosmeticRoomId = "";
    private long _lastEmoteSequence;
    public event Action? CosmeticsChanged;
    public event Action<int,CosmeticEmote>? EmoteReceived;
    public event Action<string>? CosmeticStatus;
    public IReadOnlyDictionary<int,CosmeticProfile> CosmeticProfiles { get { lock(_cosmeticLock) return new Dictionary<int,CosmeticProfile>(_cosmeticProfiles); } }
    public void ConfigureCosmetics(CosmeticLibrary library, string? deckId=null, string? mainNation=null)
    {
        if(ReferenceEquals(_cosmetics,library)) return;
        _cosmetics=library; _cosmeticDeckId=deckId; _cosmeticNation=mainNation;
        if(AdmissionState == "admitted") _ = InitializeCosmeticsAsync();
    }
    private async Task<JsonNode> CosmeticCallAsync(string method,JsonNode? args=null,CancellationToken ct=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var response=await _client.CallRawAsync(method,args??new JsonObject(),timeout.Token).ConfigureAwait(false);
        if(response.Error is {} error) throw new InvalidOperationException(error.Message);
        return response.Result??throw new InvalidDataException("外观同步响应为空。");
    }
    public async Task PublishCosmeticsAsync(CancellationToken ct=default)
    {
        if(_cosmetics is null || AdmissionState!="admitted" || IsObserver) return;
        if(!SupportsCosmetics) { CosmeticStatus?.Invoke("此房间服务器不支持外观同步，请更新房主程序。"); return; }
        await _cosmeticPublish.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profile=LocalCosmetics.Checked();
            foreach(var hash in profile.Images())
            {
                var bytes=_cosmetics.ReadImage(hash); CosmeticImage.Inspect(bytes);
                var result=await CosmeticCallAsync("cosmetics.asset",new JsonObject { ["png"]=Convert.ToBase64String(bytes) },ct).ConfigureAwait(false);
                if(result["hash"]?.GetValue<string>()!=hash) throw new InvalidDataException("外观上传校验失败。");
            }
            await CosmeticCallAsync("cosmetics.set",new JsonObject { ["profile"]=JsonSerializer.SerializeToNode(profile) },ct).ConfigureAwait(false);
            CosmeticStatus?.Invoke("外观已同步到房间。");
        }
        finally { _cosmeticPublish.Release(); }
    }
    public async Task RefreshCosmeticsAsync(CancellationToken ct=default)
    {
        if(_cosmetics is null || !SupportsCosmetics || AdmissionState!="admitted") return;
        var response=await CosmeticCallAsync("cosmetics.get",ct:ct).ConfigureAwait(false);
        var room=response["roomId"]!.GetValue<string>();
        lock(_cosmeticLock)
        {
            if(_cosmeticRoomId!=room){_cosmeticRoomId=room;_cosmeticProfiles.Clear();_cosmeticLoads.Clear();_cosmeticVersions.Clear();_lastEmoteSequence=0;}
        }
        foreach(var entry in response["profiles"]!.AsArray())
            await LoadCosmeticProfileAsync(room,entry!["seat"]!.GetValue<int>(),entry["profile"]!.DeepClone(),entry["revision"]!.GetValue<long>(),ct).ConfigureAwait(false);
        CosmeticsChanged?.Invoke();
    }
    private async Task InitializeCosmeticsAsync()
    {
        if(_cosmetics is null || _disposed || AdmissionState!="admitted") return;
        try { await PublishCosmeticsAsync().ConfigureAwait(false); await RefreshCosmeticsAsync().ConfigureAwait(false); }
        catch(Exception e) { if(!_disposed) CosmeticStatus?.Invoke("外观同步未完成："+e.Message); }
    }
    private async Task LoadCosmeticProfileAsync(string room,int seat,JsonNode node,long revision,CancellationToken ct=default)
    {
        if(_cosmetics is null || seat is <0 or >1) return;
        int generation;
        lock(_cosmeticLock)
        {
            if(_cosmeticRoomId.Length==0) _cosmeticRoomId=room;
            if(room!=_cosmeticRoomId) return;
            if(revision<_cosmeticVersions.GetValueOrDefault(seat))return;
            _cosmeticVersions[seat]=revision;
            generation=_cosmeticLoads.GetValueOrDefault(seat)+1;_cosmeticLoads[seat]=generation;
        }
        var profile=(node.Deserialize<CosmeticProfile>()??throw new InvalidDataException("外观资料为空。")).Checked();
        foreach(var hash in profile.Images())
        {
            if(_cosmetics.HasImage(hash)) continue;
            var response=await CosmeticCallAsync("cosmetics.image",new JsonObject { ["seat"]=seat,["hash"]=hash },ct).ConfigureAwait(false);
            var bytes=Convert.FromBase64String(response["png"]!.GetValue<string>());
            if(CosmeticImage.Inspect(bytes).Id!=hash) throw new InvalidDataException("远端外观图片校验失败。");
            _cosmetics.CacheImage(bytes);
        }
        lock(_cosmeticLock)
        {
            if(room!=_cosmeticRoomId || _cosmeticLoads.GetValueOrDefault(seat)!=generation) return;
            _cosmeticProfiles[seat]=profile;
        }
        CosmeticsChanged?.Invoke();
    }
    private bool HandleCosmeticNotification(RpcNotification notification)
    {
        if(notification.Method is not ("notify/cosmetics" or "notify/emote")) return false;
        if(_cosmetics is null || AdmissionState!="admitted" || notification.Params is not {} payload) return true;
        try
        {
            var room=payload["roomId"]!.GetValue<string>();var seat=payload["seat"]!.GetValue<int>();
            if(notification.Method=="notify/cosmetics")
            {
                var copy=payload["profile"]!.DeepClone();
                var revision=payload["revision"]!.GetValue<long>();
                _=Task.Run(async()=>{try{await LoadCosmeticProfileAsync(room,seat,copy,revision).ConfigureAwait(false);}catch(Exception e){if(!_disposed)CosmeticStatus?.Invoke("远端外观未能加载："+e.Message);}});
            }
            else
            {
                var sequence=payload["sequence"]!.GetValue<long>();var id=payload["id"]!.GetValue<string>();
                lock(_cosmeticLock)
                {
                    if(room!=_cosmeticRoomId || sequence<=_lastEmoteSequence) return true;
                    _lastEmoteSequence=sequence;
                }
                var emote=new CosmeticEmote(id,payload["label"]!.GetValue<string>(),payload["image"]!.GetValue<string>(),payload["style"]?.GetValue<string>()??"diy",payload["tint"]?.GetValue<string>()??"433a29",payload["enemyImage"]?.GetValue<string>()??"");
                _=Task.Run(async()=>{
                    foreach(var imageId in new[]{emote.Image,emote.EnemyImage}.Where(s=>s.Length>0).Distinct())
                    if(!_cosmetics.HasImage(imageId))
                        try {var response=await CosmeticCallAsync("cosmetics.image",new JsonObject{["seat"]=seat,["hash"]=imageId}).ConfigureAwait(false);var bytes=Convert.FromBase64String(response["png"]!.GetValue<string>());if(CosmeticImage.Inspect(bytes).Id==imageId)_cosmetics.CacheImage(bytes);}catch { /* The text remains available if an image was replaced meanwhile. */ }
                    if(!_disposed)EmoteReceived?.Invoke(seat,emote);
                });
            }
        }
        catch(Exception e) { CosmeticStatus?.Invoke("外观消息无效："+e.Message); }
        return true;
    }
    public async Task SendEmoteAsync(string id,CancellationToken ct=default)
    {
        if(!SupportsCosmetics || IsObserver || BoundSeat is null) throw new InvalidOperationException("当前会话不能发送联机表情。");
        await CosmeticCallAsync("cosmetics.emote",new JsonObject { ["id"]=id },ct).ConfigureAwait(false);
    }
}
