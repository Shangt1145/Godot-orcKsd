using Xunit;

// 宿主侧有一批**进程级静态**（引擎宿主 `OrcEngineHost`、`KeywordRegistry`、`Faction` 值域、
// 卡注册面…），多个测试类并行时它们互相串味：单独跑绿、全量跑红（`最后一击…`、
// `DebugCommands…` 长期就是这么红的）。这不是产品缺陷，是测试编排缺陷。
//
// 判据：关掉并行后全量跑两遍，同一批用例两次结果必须一致；且原先"单独跑绿"的用例不再
// 因为邻居而红。代价是总时长上升，换的是"红就是真红"。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
