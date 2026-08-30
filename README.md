# Three Unity Bridge

把 Three.js 内容接入 Unity 并打包为 Player。既支持把场景转换成 Unity 资产，也支持用 Web Bridge 原样承载完整网页游戏。

这个仓库包含两部分：

- `three-unity-bridge`：Three.js/Node.js 导出库与 CLI。
- `com.three-unity.bridge`：Unity UPM 包，提供 `.threeunity` 导入器和 Web Bridge 启动组件。
- `ThreeUnityWebHost`：Windows WebView2 宿主，把原版 Web 游戏嵌入 Unity Player，并提供 JS/Unity 双向消息通道。

开发与打包要求：Node.js 20+、.NET 8 SDK、Unity Editor；Web Bridge 生成的 Windows Player 还需要系统安装 WebView2 Evergreen Runtime。

## 当前可用闭环

1. 在 Three.js 中构建场景。
2. 调用 `exportThreeUnityJson(scene)` 生成一个 `.threeunity` JSON 资产。
3. 在 Unity 项目中安装 `unity-package`。
4. 把 `.threeunity` 放进 Unity 的 `Assets/`。
5. Unity 自动生成一个可拖入 Scene 的 Prefab 型资产；它引用的 Mesh、Material 和 Texture 都作为子资产保存，因此会正常进入 Player Build。

## 快速试用

```powershell
npm install
npm run example
npx three-unity install-unity C:\Path\To\UnityProject
npx three-unity copy .\examples\output\three-unity-demo.threeunity C:\Path\To\UnityProject
npx three-unity build-unity .\scene.threeunity C:\Path\To\UnityProject
```

也可以在 Unity Package Manager 中选择 **Add package from disk**，打开：

```text
unity-package/package.json
```

导入后，选中 `.threeunity` 可在 Inspector 中决定是否导入 Camera、Light、MeshCollider。
也可以从 Unity Package Manager 的 **Samples** 导入 `Imported Triangle`，直接检查最小资产。

## Three.js 代码接入

```ts
import { exportThreeUnityJson, downloadThreeUnity } from "three-unity-bridge";

const json = await exportThreeUnityJson(scene, {
  name: "Level 01",
  unitScaleMeters: 1,
  // Three.js cameras are often passed directly to renderer.render() and are
  // not children of scene. Include those detached render objects explicitly.
  extraObjects: [gameCamera],
});

downloadThreeUnity(json, "level-01.threeunity");
```

Node.js 中可直接把返回字符串写入文件。浏览器纹理会被转成 PNG data URL，`DataTexture` 会以 RGBA8 内嵌。

## 数据驱动的可玩转换

导出器可以把 Unity 运行时配置一起写进 `.threeunity`。游戏差异只存在于一小段 Three.js 适配数据中，Unity importer 和运行时代码不按游戏名称分支：

```ts
const json = await exportThreeUnityJson(scene, {
  extraObjects: [camera],
  runtime: {
    controller: "first-person",
    colliderMode: "mesh",
    enableBlockEditing: true,
    allowFly: true,
    hudStyle: "voxel-hotbar",
    hotbar: blockTypes.map(({ name, color }) => ({ name, color })),
  },
});
```

Unity 导入后选中该 `.threeunity`，执行 **Assets > Three Unity > Create Playable Scene**。通用 builder 会根据配置生成碰撞体、相机、控制器和 HUD，不需要为每个游戏再写 Unity 建场脚本。

需要直接打包时可跳过 Unity 手工操作：

```powershell
npx three-unity build-unity .\little-cubes.threeunity C:\Path\To\UnityProject `
  --unity "C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe" `
  -o .\Build\LittleCubes.exe
```

该命令会安装/更新 bridge 包、复制转换资产、按 runtime profile 自动生成 Scene，并调用 Unity 批处理构建 Windows Player。

如果目标是保持原版网页游戏的画面、DOM UI、JavaScript 逻辑和存档，使用 Web Bridge 模式：

```powershell
npx three-unity build-web-unity .\dist C:\Path\To\UnityProject `
  --name LittleCubes `
  -o .\Build\LittleCubesWebBridge\LittleCubesWebBridge.exe
```

Web Bridge 会把原始 `dist` 不改内容地复制进 Unity `StreamingAssets`，发布一个 WebView2 宿主，并将其嵌入 Unity Player 窗口。`window.chrome.webview.postMessage(...)` 发出的消息会通过命名管道送到 `ThreeUnityWebBridgeLauncher`，Unity 也可以用 `SendToWeb` 反向发送消息。省略 `-o` 时，CLI 默认输出到 Unity 项目的 `Build/<name>/<name>.exe`，整个 `<name>` 目录可以直接压缩分发。

### 可复用的 Unity 权威逻辑

`build-web-unity` 默认只负责原样打包；加上 `--logic-profile` 后，同一个 Player 还会启用对应的 Unity C# 逻辑模块。Web 侧用 `three-unity-bridge/logic` 的版本化协议客户端连接，不需要针对游戏改宿主、CLI 或 Unity 建场脚本：

```powershell
npx three-unity build-web-unity .\dist C:\Path\To\UnityProject `
  --name NameToShopLogicBridge `
  --logic-profile shop-flight-v1 `
  -o .\Build\NameToShopLogicBridge\NameToShopLogicBridge.exe
```

当前内置 profile：

- `voxel-player-v1`：Unity 负责玩家移动、跳跃/飞行与体素碰撞，Web 保留 Three.js 渲染和 UI。
- `shop-flight-v1`：Unity 负责店铺起飞/降落缓动、飞行时钟、位置与旋转，Web 保留店铺生成、WebGPU/WebGL 渲染、DOM HUD、相机、音频、导出和存档。

游戏侧只需一个薄适配层：提供当前快照、把 Unity 状态应用回 Three.js，以及保留原 JavaScript 帧函数作为回退。通用握手、序列号、generation 隔离、首状态超时和运行中 watchdog 都在 SDK 中。每次连接使用独立的紧凑随机 `sessionId`；fallback 是不可被迟到状态重新打开的粘滞边界。只有 Unity 的 `ready.features` 明确声明 `session-restart-v1` 时，适配器才会自动重连、重建 Unity 模块并从当前 JavaScript 快照重新 bootstrap；未声明该能力时保持 JavaScript 回退，不进行无效重试。可复制的接入文件见 [`examples/logic-adapters/name-to-shop/unity-flight-adapter.js`](examples/logic-adapters/name-to-shop/unity-flight-adapter.js)。Unity 未启用 profile、WebView2 不可用、协议不匹配或状态中断时，适配器会继续/恢复原 JavaScript 逻辑。

可选的 `runtime-lifecycle-v1` 把 Unity Player 的聚焦/暂停状态桥接给网页，而不是依赖嵌入 WebView 不可靠的可见性事件。Unity 只会在双方声明能力、当前会话 `bridge.ready` 已先发出后发送 `runtime.lifecycle.state`；快速变化按会话只保留最新状态，网页用 `runtime.lifecycle.ack` 确认。`RuntimeLifecycleGate` 默认始终运行，只有收到合法的新 revision 才暂停昂贵帧；未协商、旧包、坏消息或回调异常都会安全恢复原网页执行。name-to-shop 用它跳过后台 tween、场景更新和渲染并暂停 Web Audio，恢复时不补跑积压帧；DOM UI、原版 Three.js 渲染和 JavaScript fallback 并未转写到 Unity。

兼容方向是单向保守的：新版 Unity 包仍接受没有 `sessionId` 的旧版 Web 客户端；但使用会话隔离的新版 SDK 连接旧版 Unity 包时，不会接受缺少匹配 `sessionId` 的回复，也不会进入 Unity 权威状态，而是在握手超时后安全留在原 JavaScript 逻辑。要启用会话重建，应同时升级 Web SDK 和 Unity 包；旧客户端兼容不等于支持 `session-restart-v1`。

这条链路转换的是已支持的运行时语义；任意 JavaScript 逻辑仍不能自动翻译为 C#，需要映射到现有 profile 或新增一个可复用的 SDK 适配器与 Unity 模块。

### 性能、背压与遥测

Unity→Web 的管道写入由专用后台线程完成，不阻塞 Unity 的 `Update` / `FixedUpdate`。控制类消息进入有界可靠队列；`*.state` 之类的实时流按 `sessionId + 消息类型` 只保留最新值。逻辑会话切换时会原子清除旧 owner 的可靠消息和 latest 状态；连续发送 32 条可靠消息后强制让出一次 latest 槽位，避免实时状态永久饿死。队列满表示可重试的 `backpressure`，只有连接退役时仍未送达的可靠消息才计入 `dropped`。

内置逻辑模块在序列化协议包时会把已知的 `type`、`sessionId` 与 JSON 一起排队，Bridge 不再为了判断 reliable/latest 和 stream key 而在 Unity 主线程反序列化自己刚生成的 JSON。只实现旧接口的第三方模块仍能使用，路由器会为它们保留一次兼容解析；`metadataFast` 与 `metadataFallback` 分别记录两条路径。每次 `FlushOutgoing` 最多接受 256 条消息，模块突发输出的余量保序留到后续 Unity 回调，避免一帧内把整条 1,024 容量队列搬满。

WebView Host 使用单一异步写入泵保证 Web→Unity 消息顺序，并把 Unity→Web 的 UI 投递按批次合并，两个方向都设有 1,024 条硬上限。页面导航完成只证明原站可显示；Unity→Web 消息必须同时等到当前 document 的 WebView listener ACK。新 document 会重置监听 latch，并忽略被重定向替代的旧导航 completion；hash/pushState 不创建 document，因此不会误触发 Host 重启。每个 Host 在创建 WebView2 子进程前先加入 Windows Job，退役时只有 Job 报告 `ActiveProcessCount=0` 才允许启动下一代，首次 `TerminateJobObject` 失败会节流重试。

内置逻辑模块还使用通用状态发送门：状态变化时立即发送，状态不变时抑制重复快照，并每 200ms 保留一次低频心跳以满足 500ms watchdog。Web→Unity 可复用 `RealtimeInputGate`，把渲染帧输入变成“数字边沿立即发送、模拟量限频、静止心跳”，并可合并保留单帧动作；Unity 端的 `ThreeUnityInputFreshnessGate` 会在心跳中断后清除旧的移动/跳跃状态，防止 WebView 卡住时角色持续失控。

Player 每 120 个物理帧输出一条 `THREE_UNITY_BRIDGE_PERF`，包含收发消息/字符数、合并数、可靠队列背压、实际丢失、owner 清理、公平调度、当前/历史最大积压，以及状态发送、抑制、心跳、会话重建、拒绝、元数据快路径、flush 预算和生命周期发送/确认计数。Web 侧 `LogicClient.metrics` 提供对应的消息量、字符量、最新值合并、过期序列/异会话/终态尾包拒绝、协议错误、回退和重连计数。

`name-to-shop` 的同机实测中，静止商店跑到 240 个 Unity 物理帧时，Unity→Web 从 184 条 / 40,166 字符降到 20 条 / 4,262 字符，分别减少 89.1% 和 89.4%。出站分类基准对同一协议包重复 25,000 次：本轮头部解析用了 523,127 个 `Stopwatch` ticks，随包元数据只用了 6,124 ticks，并避免了全部 25,000 次解析。另一项确定性突发基准一次排队 4,096 条可靠消息，首轮只搬运 256 条、其余 3,840 条保序留存。重建后的实体 Player 报告 `metadataFast=21 metadataFallback=0 flushBudgetStops=0 maxFlush=2 lifecycleEmitted=2 lifecycleAck=2 lifecycleAckRejected=0`。LittleCubes 的 session-aware 240 FPS 输入基准把 Web→Unity 消息从 2,400 条降到 140 条（94.2%），协议字符减少 93.9%；真实 Player 的空闲输入由 180 条/秒降到约 4 条/秒，且 `dropped=0`。体素窗口复用使碰撞采样减少 90.8%，计入每条消息的 `sessionId` 后，`collision-delta-v2` 仍使协议字符减少 45.2%。Unity `6000.3.22f1` 的真实故障注入已分别验证两个 profile：LittleCubes 在输入失效后完成一次会话重建、新会话 ready 和后续逻辑 tick；name-to-shop 在 Web 请求回退后同样完成一次重建、新 ready、生命周期重新确认和后续 tick。两次运行都没有遗留孤儿 Host，性能标记均为 `dropped=0`。关闭 Player 后，嵌入式 WebView Host 在先前实测中于 140ms 内随父进程退出。测试方法、日志字段和扩展规则见 [`docs/BRIDGE_PERFORMANCE.md`](docs/BRIDGE_PERFORMANCE.md)。

## 把游戏语义传给 Unity

Three.js 的 `userData` 会保留到 Unity 的 `ThreeUnityMetadata`：

```ts
mesh.userData = {
  gameplayTag: "door",
  unity: {
    components: [
      { type: "Door", data: { locked: true, keyId: "red-key" } },
    ],
  },
};
```

第一版不会根据字符串自动执行或生成 C#，避免导入资产时运行不受信代码。游戏项目可以读取 `ThreeUnityMetadata.Components`，用自己的白名单工厂把 `Door`、`SpawnPoint` 等描述转换成真实 MonoBehaviour。

## 转换规则

- Three.js 与 Unity 都是 Y-up，但手性不同。导入器会反转 Z、转换 Quaternion，并把每个三角形的索引绕序反转一次，使镜像后的几何继续朝外渲染。
- `unitScaleMeters` 在 Unity 导入时应用于位置、相机裁剪面和灯光范围。
- 支持普通 `BufferGeometry`，包括 position、normal、uv、vertex color、index、groups/submeshes。
- 支持 MeshBasicMaterial、MeshStandardMaterial 的基础颜色、金属度、粗糙度、透明、双面、发光与常用纹理。
- 支持 Perspective/Orthographic Camera，以及 Directional/Point/Spot/Ambient Light。
- Built-in Render Pipeline 和 URP 会自动选取各自可用的 Lit/Unlit Shader。

## v0.1 边界

这是一个场景与资产桥，不是 JavaScript 到 C# 的通用编译器。当前不转换：

- 任意 JavaScript 游戏逻辑、DOM/UI、WebXR 和自定义 GLSL Shader。
- SkinnedMesh、骨骼动画、morph target、粒子与后处理。
- HDRP 专用 Shader 映射。
- 外链纹理；纹理必须能在导出时读取并嵌入，跨域图片需配置 CORS。

这些边界会作为导出 warnings 写入文件，并显示在 Unity Import Log。后续最值得扩展的是 AnimationClip/SkinnedMesh，以及可配置的“组件描述 → 项目 C# 类型”白名单映射。

## 开源游戏实转

仓库中的 `conversions/` 包含 Voxel Frontier、LittleCubes、Warptracker 三个场景资产实转，以及 name-to-shop / LittleCubes 的 Web Bridge + Unity 权威逻辑实测。前三份文件均通过 CLI 校验、Unity 6 批处理导入和 `StandaloneWindows64` Player 构建；name-to-shop 保留原版 UI/渲染，并把飞行模拟剥离到 `shop-flight-v1`；LittleCubes 则把玩家运动和体素碰撞交给 `voxel-player-v1`。详见 [`conversions/name-to-shop-logic/RESULTS.md`](conversions/name-to-shop-logic/RESULTS.md) 与 [`conversions/little-cubes-logic/RESULTS.md`](conversions/little-cubes-logic/RESULTS.md)。

Unity 运行时包还提供两类可选控制器：`ThreeUnityFirstPersonController`（WASD、鼠标视角、跳跃，以及可选的方块挖掘/放置）和 `ThreeUnityOrbitShowcaseController`（平移、环绕、缩放）。它们不会把任意 JavaScript 自动翻译成 C#，但能为转换产物建立可操作的验收入口。详见 [`conversions/RESULTS.md`](conversions/RESULTS.md)。

## 开发验证

```powershell
npm test
npm run build
npx three-unity validate .\examples\output\three-unity-demo.threeunity
```
