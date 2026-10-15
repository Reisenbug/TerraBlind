# 内置 Agent

在游戏内的模组配置 → TerraBlind → 内置 Agent 中填写：

- Agent API Key：通用模型服务商的密钥。
- Agent Base URL：OpenAI 兼容根地址，例如 `https://api.openai.com/v1`；也接受完整的 `/chat/completions` 地址。
- Agent 模型名：服务商的准确模型 ID，必须支持 Chat Completions 工具调用。
- 回复语言风格：可填写角色的说话方式。

这些设置是 ClientSide，只保存在本机 ModConfigs。Jev 仍使用单独的 TypeSafe Key。API Key 不写进源码、不通过多人协议同步。

```text
/tb 帮我合成一个工作台，有不清楚的就问我
/tb 十个
/tb stop
/tb status
```

Agent 提问时，下一条 `/tb` 是当前任务的回答。执行中收到其他指令会取消旧动作，把新要求交回同一段对话处理。`/tb stop` 和 `/halt` 都会停止当前 agent。离开世界会取消任务并清空本次世界对话。

多人模式：AI 客户端开启 `AiClient`，绑定后由所有者发送 `/ai <编号> task <自然语言>`；提问的回答也走这个入口。回复回传给绑定的所有者。普通玩家客户端不运行 AI 工具。

迁移了 `second_player.py` 的 23 个工具：状态查询、Wiki、寻找地形与方块、导航、采矿、物品使用、放置、交互、战斗、开箱取物、合成、配方、台账、动作原语、询问和说话。增加 `set_combat`，可禁止默认自动走位或攻击，许可持续到再次修改或离开世界。

原脚本的 find 模板、`agent_loop.py` 预生成计划分支、导航时自动绕路捡箱子没有移植；现在统一使用逐步工具调用循环。原有 mod 速通流程保留独立入口。

模型请求在后台执行。游戏工具通过主线程队列直接调用共享分发器，不经过本机 HTTP，也不需要 Python。动作完成后返回当前位置、背包真实增减和台账进度。工具参数错误返回模型修正；网络或鉴权错误停止任务并报告。

默认最多执行 40 轮模型调用；历史中旧的大块工具结果会压缩，最近结果保留。用下面的离线测试检查模型协议、工具续接、参数错误、取消和历史完整性，不产生 API 费用：

```sh
dotnet run --project tests/Agent/AgentTests.csproj
./check.sh
```

编译和离线测试不能代替实机验证。建议依次测试纯聊天、询问并回答、合成、导航中停止，以及“Boss 战不自动走位”的许可覆盖。

离线测试共 12 组，结束标记为 `AGENT_CHECKS_COMPLETE groups=12`。除模型协议外，还直接链接生产的 NativeAgent、AgentTools 和 AgentNetwork，检查取消后的主线程队列、任务替换、战斗许可、背包增减统计，以及真实格式数据包的绑定、任务执行、回复拆分和掉线停止。Terraria 对象、动作端点和网络发送使用替身；这不验证真实合成、人物物理、TCP 或双客户端同步，也没有执行 Combat/Dodge 的逐帧控制逻辑。
