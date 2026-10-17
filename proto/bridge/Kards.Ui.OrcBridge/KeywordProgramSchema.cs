using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Game.Cards;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 词条程序模型的 UI 转发面（架构红线：UI 装配不得直接引用 Orc.Game）。
/// UI 编辑器只操作 JSON 文本与 JsonNode；解析、校验、种子与词表全部经本类代理到引擎模型。
/// </summary>
public static class KeywordProgramSchema
{
    public static string[] Rules => KeywordProgram.RuleNames;
    public static string[] Events => KeywordProgram.EventNames;
    public static string[] Subjects => KeywordProgram.Subjects;
    public static string[] Targets => KeywordProgram.Targets;
    public static string[] Operations => KeywordProgram.Operations;
    /// <summary>全新文档（引擎默认骨架，含全部字段）。</summary>
    public static string DefaultDocument()=>JsonSerializer.Serialize(new KeywordProgram(),KeywordProgram.Json);
    /// <summary>元素种子：rules / handlers / skills / when / 其它（动作）。返回 JSON 文本。</summary>
    public static string Seed(string key)=>key switch
    {
        "rules"=>JsonSerializer.Serialize(new KeywordRulePatch(),KeywordProgram.Json),
        "handlers"=>JsonSerializer.Serialize(new KeywordEventHandler(),KeywordProgram.Json),
        "skills"=>JsonSerializer.Serialize(new KeywordActiveSkill(),KeywordProgram.Json),
        "when"=>JsonSerializer.Serialize(new KeywordCondition(),KeywordProgram.Json),
        _=>JsonSerializer.Serialize(new KeywordProgramAction(),KeywordProgram.Json)
    };
    /// <summary>
    /// 解析＋规范化（fail-fast 校验、剥离未知字段、回序列化为完整骨架）。
    /// 成功＝true 且 <paramref name="document"/> 为规范化文本；失败＝false 且 <paramref name="error"/> 带原因。
    /// </summary>
    public static bool TryNormalize(string json,out string document,out string error)
    {
        document="";error="";
        try{document=KeywordProgram.Parse(json).ToJson();return true;}
        catch(Exception e)when(e is InvalidDataException or InvalidOperationException or ArgumentException or JsonException){error=e.Message;return false;}
    }
}
