# RL 移动小实验

这是一套独立实验，不加载 TerraBlind，也不修改正常游戏或服务器。
模型使用 PPO，输出六种方向/跳跃按键组合，每个动作持续最多四帧。
观察包括相对目标、真实速度、额外跳可用状态、局部实心格和上次按键；堆叠最近四次观察。
不提供装备编号。每回合从普通角色、云朵瓶、红气球中随机选一种训练，
云朵瓶加红气球的组合作为留出测试。目标是到达随机高度和距离的台阶上方。

## 环境边界

测试模组在 `-rlport` 专用启动参数存在且运行于服务器时才启用。
加载真实 tModLoader、临时世界和实际饰品，通过完整 `Player.Update` 推进角色，
因此装备效果、跳跃和碰撞由游戏代码执行，不是 Python 重写的物理。
命令处理期间阻塞正常世界循环，显式推进角色，不推进其他实体、世界事件及正常帧时钟。
这是引擎内角色运动实验，不是完整游戏帧、真实客户端、联机或复杂模组兼容性验证。
它不训练翅膀、钩爪、坐骑，也不证明能适应所有饰品。

## 运行

在仓库根目录执行，默认路径适用于本机 macOS arm64：

```sh
uv venv --python 3.12 /tmp/terrablind-rl-venv
uv pip install --python /tmp/terrablind-rl-venv/bin/python -r tests/Rl/requirements.txt
dotnet build tests/Rl/Rl.csproj '-p:ExtraBuildModFlags=-tmlsavedirectory /tmp/terrablind-rl-lab'
/tmp/terrablind-rl-venv/bin/python tests/Rl/train.py --smoke-only
/tmp/terrablind-rl-venv/bin/python tests/Rl/train.py --steps 32768
```

脚本启动自己的服务器进程，默认游戏端口 17977，训练接口仅监听 127.0.0.1:19041。
先检查四种装备的实际水平位移、跳高以及额外跳效果，再检查 Gymnasium 接口并训练。
每次评估使用固定的装备、台阶高度和种子，与随机按键基线比较。
生成文件全部位于 `/tmp/terrablind-rl-lab`：

- `engine.log`：本次隔离引擎输出。
- `ppo_movement.zip`：训练好的策略参数。
- `result.json`：步数、耗时、实际运动检查和分组评估。
- `replay.html`：无需外部依赖的交互回放，可切换装备、台阶高度，拖动时间轴。

重置检查会拒绝角色被世界边界挤走的场景。到达必须在目标附近实际接触支撑，
不能把跳跃顶点速度为零当作落地。评估每个装备/高度组合各 10 局，包含失败回放。

成功率必须分装备和地形比较，尤其是留出组合。一次短训练只检查这条路线能否学习，
不是通用控制器的验收。短历史也不等于模型已学会主动测量、理解陌生饰品。
当前评估只有 0/3/5 格台阶，普通跳也可通过，因此即使成功率很高，
也不能据此认定策略学会了二段跳或适应装备；下一步需要必须使用额外能力才能通过的场景。
