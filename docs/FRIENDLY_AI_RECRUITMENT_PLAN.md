# 友军 AI 征用：精简 MVP 草案
> **一句话方案：** 服务器验证并征用允许的友军固定翼 AI，把它作为玩家会话中的 Wingman（僚机名册成员）管理；客户端只发送目标 PID 和命令意图。
>
> **状态：** 内部 MVP review 草案，不表示任何功能已经实现。
>
> **基线：** Nuclear Option 0.34（build 24403978），Loyal Wingman 0.0.2。
## 1. 本次 review 要决定什么

本草案定义最小、安全的“征用友军 AI”路径，供非开发者快速确认范围。
先取得只读证据，再改变 AI；不以修改网络所有权或删除多人限制换取表面可用。
### 已决定

- **服务器是唯一事实来源。** 客户端只发送 target PID 和命令意图；资格、占用、征用、命令、释放和僚机名册状态均由服务器执行。
- 被征用飞机始终是 Wingman（僚机名册成员），绝不成为母机、`Home`、Leader 或 `RecoveryProvider`。
- 玩家当前 `Player.Aircraft` 只是编队锚点，不在僚机名册里；换飞机时只更新锚点。
- 不改变原生飞机的 Mirage ownership/client authority、`playerRef`、`LocalSim`，也不接管原生物理同步。
- MVP 不做直接 `TAKE CONTROL`。将来如要支持，必须先从僚机名册释放，再走独立原生切换流程。
- FQ/Blueprinter/Kestrel 是可选集成；FQ 保持独立的发射/回收路径。
- 复用 `SessionRegistry.TryAttachDrone` 和 `WingmanSessionTransport`；不另建网络框架，不给原生飞机外挂 `NetworkBehaviour`。
### MVP 不做

- 不支持直升机、倾转旋翼、任务脚本单位、降落/滑行/停放中的飞机。
- 不开放 combat、原生 RTB 或全部机型；这些留给后续独立 review。
- 不把“删除单人 guard”当作多人支持。
## 2. 当前代码切口

- `src/Plugin.cs` 目前把 Blueprinter 和 Kestrel 声明为 HardDependency，且启动会等待 Blueprinter；这是可选化的首要切口。
- `src/Sessions/CarrierSession.cs` 的 `CarrierSession.Home` 当前同时服务 carrier、回收、cruise 和会话路由；原生被征用飞机不能使用或重定义它。
- `src/Sessions/SessionRegistry.cs` 的 `TryAttachDrone` 已有“注册路由、加入僚机名册、注册 AI 管理器、失败回滚”的顺序，可扩展为来源无关的 attach。
- `src/Networking/WingmanSessionTransport.cs` 已有服务器 RPC、定向结果和完整 snapshot 的基础；扩展它，不复制第二套 transport。
- 当前 `MapCommandPanel` 有 FQ 文案和本地命令假设；MVP UI 只把它改成发送意图并显示服务器结果。
## 3. 最小模型

服务器维护一个 `PID -> OwnerSession` 占用表。它只表示“此 PID 当前被哪个玩家会话征用或预留”，不等同于 Mirage owner。
每个玩家只有一个简单 Wingman session：

- Owner；
- 当前编队锚点（已验证的 `Player.Aircraft`）；
- 僚机名册；
- session revision；
- 可选 FQ 上下文（仅 FQ 发射、回收和 carrier 路径使用）。
每个僚机名册条目只保存：PID、`Aircraft`、Source、必要命令状态和编队位置。
```csharp
enum Source { RecruitedNative, SpawnedFq }
enum Mode { Auto, Follow, Loiter, StandDown }
// 条目：pid、aircraft、source、mode、编队位置
```
状态只使用：`Available -> Managed -> Released/Lost`。
如果原生 AI 恢复失败，记录错误并在本场景禁止再次征用该 PID；不设计额外的大型状态机。
## 4. 服务器资格检查

服务器按以下关键项检查目标；任何失败都返回稳定的拒绝原因，不改变目标：
1. PID 能解析为有效、存活的 `Aircraft`，且未被占用。
2. 目标与玩家同阵营、同可用网络上下文，且无人驾驶。
3. 目标是服务器本地模拟：`IsServer` 与 `LocalSim` 均为真，且没有客户端 authority。
4. 目标是普通固定翼：`PilotType.Plane` 加 `AutopilotPlane`。
5. 目标处于标准原生 combat AI state，不是任务脚本、起降或停放过渡 state。
6. 目标机型位于首批 allowlist。
建议的原因包括：`already_recruited`、`not_friendly`、`player_controlled`、`not_server_sim`、`client_authority_present`、`fixed_wing_required`、`native_state_required`、`not_allowlisted`。
## 5. 占用、征用与释放

同一服务器游戏线程内采用 first-writer-wins。完成同步资格检查后立即 `TryClaim(pid, session)`：第一位成功，第二位得到 `already_recruited`。attach 失败必须立即 rollback；将来若事务跨帧，跨帧前持续持有 claim。
```csharp
if (!Eligible(target, session, out reason)) return Reject(reason);
if (!claims.TryClaim(pid, session)) return Reject("already_recruited");
if (!TryAttachManaged(target, Source.RecruitedNative, out reason)) {
    claims.Release(pid, session); return Reject(reason);
}
PublishSnapshot(++session.revision);
```
`TryAttachManaged` 应复用 `SessionRegistry.TryAttachDrone` 的注册与回滚纪律，但不能把原生飞机写进 `Home` 或 FQ recovery 路径。
释放时，服务器先停止 LW 命令，再检查目标仍为 LW 安装的 state、无人驾驶且仍由服务器本地模拟。只有这些条件都满足，才创建**新的**原生 combat AI state，然后移除条目和 claim。

不得复用旧 AI state；不得改变 ownership、authority、`playerRef` 或 `LocalSim`。恢复失败时记录错误，并在本场景拒绝再次征用该 PID。
## 6. 命令、同步与 UI

首批命令只有：`FOLLOW`、`LOITER`、`AUTO`、`STAND DOWN`、`RELEASE`。每条命令均由服务器验证目标属于该 Owner 的僚机名册，再执行。
combat 与 RTB 后续再加，且必须单独验证；FQ 的现有回收仍属于可选 FQ 路径。

客户端请求只含 target PID 和有界命令意图。`WingmanSessionTransport` 返回结果，并发布：
- session revision；
- 完整僚机名册；
- 每条目的必要 mode。
客户端只接受更新 revision；late join 主动请求完整 snapshot。客户端不本地裁定资格、不乐观加入条目，也不管理原生物理、伤害或武器复制。

地图必须显式选择友军 AI，显示 `RECRUIT`/`RELEASE` 及服务器拒绝原因。行项目使用通用 callsign/机型标签，不使用硬编码 FQ 文案；被征用飞机只显示为 Wingman。
## 7. 简短生命周期规则

| 事件 | 服务器处理 |
| --- | --- |
| 目标死亡/销毁 | 从僚机名册与占用表移除，标记 Lost。 |
| 玩家登机 | 先尝试安全释放；不能继续作为 Wingman。 |
| Owner 断线 | 释放管理，但绝不销毁原生飞机。 |
| 场景切换/server stop | 清空会话与占用表，不保存旧对象引用。 |
| 玩家换飞机 | 重验证并更新编队锚点，不改僚机名册归属。 |
| 阵营变化 | 停止命令并释放；无法安全恢复则记录并禁用再次征用。 |

所有路径都要复查：`Identity.Owner`、`HasAuthority`、`playerRef`、`LocalSim`、`NetworkHQ` 未被 LW 改变。
## 8. FQ 可选化

1. 将 `Plugin.cs` 的 Blueprinter/Kestrel HardDependency 改为 soft dependency。
2. 核心 session、PID 占用表、transport 和地图意图不等待 BP；无 BP 时仍可运行核心征用路径。
3. `PayloadRegistry`、cradle、port、FQ loadout/recovery/cruise 只在依赖可用时初始化。
4. cleanup 按 Source 分支：`SpawnedFq` 保留既有 FQ 发射/回收清理，`RecruitedNative` 只做原生 AI 释放。
## 9. 五个实施阶段

### Phase 1：baseline + optional startup
- 确认 0.34 的标准原生 combat AI state、玩家关联和固定翼判定。
- 完成 soft dependency 启动拆分；无 BP 与有 BP 都能构建。
- **验收：** 无 BP 核心启动；有 BP 的既有 FQ 发射/回收回归不变。

### Phase 2：session/PID 表重构（行为不变）
- 增加 `PID -> OwnerSession` 占用表和 Source 字段。
- 将 FQ 专有 `Home` 语义隔离，不改变现有 FQ 行为。
- **验收：** 现有逻辑测试和 FQ 流程无行为变化。

### Phase 3：server-only read-only Probe
- 只实现资格检查与原因日志/只读 UI 提示。
- 不 claim、不 attach、不改 AI state。
- **验收：** allowlist、无人驾驶、固定翼、authority 等拒绝原因可重复验证。

### Phase 4：SP/Host adopt-release
- 实现 claim、attach、释放、恢复和失败 rollback。
- 只开放首批固定翼 allowlist；不做 MP remote 命令、combat 或 RTB。
- **验收：** SP/host 可征用和释放，且网络字段不变。

### Phase 5：MP recruit/release + safe commands
- 扩展 `WingmanSessionTransport` 与 snapshot，客户端只发意图。
- 加入首批五个命令和地图显式选择。
- **验收：** remote、竞争、late join、断线均由服务器正确处理。

## 10. 最关键测试矩阵

- SP；listen host；remote client；dedicated server（仅在准备宣称支持时）。
- 两玩家同时抢同一 PID；late join；Owner 断线；玩家登机；目标死亡；换玩家飞机。
- 无 BP；有 BP；FQ 与原生 Wingman 混合。
- 每次征用、命令、释放和失败后，验证 `HasAuthority`、`playerRef`、`LocalSim` 及 ownership 未变。
- 自动化至少覆盖 eligibility、first-writer-wins、attach rollback、释放失败禁用、revision 更新和 snapshot 刷新。
## 11. 待用户 review（建议默认值，但未决定）

| 项目 | 建议默认值 |
| --- | --- |
| 每玩家最多数量 | 4 架。 |
| 征用方式 | 地图显式选择，不自动征用。 |
| 距离限制 | 首版不加硬距离限制，仅要求同可用网络上下文。 |
| 首批 allowlist | 初始为空；只加入实机验证过的普通固定翼机型。 |
| MP 安装要求 | 首版建议所有使用 MP 操作 UI 的客户端安装 LW；最终策略待确认。 |
## 12. 建议下一步

用户批准后先做 Phase 1–3，只获得启动与只读 Probe 证据；在这些证据通过前，不改变 AI，不开放征用，也不宣称多人支持。
