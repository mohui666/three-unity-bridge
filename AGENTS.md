# AGENTS.md

## Astra 协作约定

- 以用户当前目标和本轮明确约束为准。任务要求实施时，完成实际修改与必要验证，不停在计划、建议或“是否继续”。普通实现选择自行决定；只有缺失信息会实质改变结果或操作超出授权时才询问，并先完成不依赖答案的工作。
- 用户指令优先于本地 skills 的工作流建议。只读取与当前任务直接相关的文件和技能；不因关键词命中就串联整套技能、生成流程工件或增加审批。
- 保留既有业务规则、数据所有权、用户改动和明确的工具限制。只改当前目标需要的内容，不顺手重构、升级依赖、搬目录或扩展产品范围。

## 拒绝过度防御性编程

- 直接使用已有输入、文件、依赖和运行环境，不重复做环境、权限、目录或文件存在性预检查。
- 不为假想故障添加重复参数验证、大量极端输入分支、宽泛 `try/catch`、默认值兜底、静默失败或伪造成功。契约不满足时暴露具体错误。
- 不主动新增重试、退避、熔断、降级、备用实现、兼容层、自动备份、回滚、迁移或恢复机制。
- 不主动添加 SHA、MD5、签名、文件哈希、完整性校验、CI/CD、发布门禁、安全扫描、许可证审计、复杂日志、监控、遥测或诊断框架。
- 不为未来需求预建插件系统、通用框架或抽象层，不为小改动铺设大量单元测试、回归测试、故障注入或性能基准。
- 只在缺少检查会立即阻止核心功能、造成明显数据损坏或掩盖真实错误时保留最小必要检查。现有鉴权、真实业务校验和数据保护功能继续遵守其契约；本规则不授权删除这些功能。
- 例外必须来自用户明确要求，或与本次改动直接相关的既有产品契约。旧文档中泛化的“每次全量检查”“必须先审批”“自动完善”不构成额外任务。

## 验证与交付

- 选择能证明本次行为的最小验证：文档或提示词改动检查内容和 diff；代码改动运行相关构建、现有定向测试或核心流程冒烟。低影响、可逆改动不新增仅复述实现的测试。
- 必要检查通过即交付；只有新改动、失败或具体未解决疑点才扩大或重复验证。不要为了收尾重跑无关全量测试、打包、实机流程或基准。
- 错误如实报告。区分实际运行通过、静态检查、未运行与真实环境验证；历史测试数量不能当作本次证据。
- 仅在任务需要时使用子代理；不强制委派、切换模型或修改推理档位，遵守当前会话设置与工具权限。
- 按当前授权和项目约定执行 Git 操作，只提交本任务文件；不要为清空工作区而夹带其他改动，不强推或丢弃用户内容。没有远端时报告，不擅自创建远端。
- 用简明中文交代实际修改、验证结果和已知问题。只有需求、接口或已验证事实改变时同步相关文档，不追加与交付无关的报告。

## Mission

This repository builds a reusable Three.js-to-Unity bridge. Preserve the original web game's rendering, DOM UI, input, audio, assets, and save behavior while moving only suitable runtime responsibilities to Unity. Changes must improve the generic bridge rather than hard-code one game.

## Repository map

- `src/`: TypeScript exporter, CLI, protocol, browser logic client, transport helpers, and reusable simulation helpers.
- `tests/`: Node test suite for the TypeScript API and protocol.
- `webview-host/`: .NET 8 Windows WebView2 host embedded into the Unity Player window.
- `webview-host-tests/`: .NET tests for host lifecycle and recovery behavior.
- `unity-package/`: Unity UPM package. Runtime, Editor tooling, shaders, samples, and EditMode tests live here.
- `examples/`: minimal exporter and reusable browser-side logic adapters.
- `benchmarks/`: deterministic transport and collision benchmarks.
- `conversion-tools/`: repeatable capture and physical recovery harnesses.
- `docs/`: architecture, protocol, performance, and validation notes.
- `conversion-work/`, `conversions/`, `unity-smoke/`, and `unity-winding-verify*/`: ignored local validation workspaces and generated evidence. They may contain upstream clones, converted samples, reports, or generated Unity projects; never stage them.

## Non-negotiable invariants

1. Web Bridge packaging must preserve the source `dist` bytes and relative paths. Do not replace the original game with a visual approximation.
2. DOM UI, CSS, Three.js/WebGPU/WebGL rendering, input, audio, persistence, and browser fallbacks must keep working unless a documented bridge capability explicitly owns that behavior.
3. Browser/Unity protocol messages are versioned and session-scoped. A restarted session must not receive queued messages from an older session.
4. Reliable messages retain order and are retried without duplication. Realtime state is bounded and coalesced by session plus message type; it must not starve forever behind reliable traffic.
5. Unity's main thread must not block on pipe or WebView I/O.
6. A Host generation may create WebView child processes only after it is assigned to its Windows Job. A replacement Host must not launch until the retired Job reports zero active processes.
7. Unity-to-Web startup messages may be released only after navigation and the browser message listener are both ready.
8. Recovery telemetry must distinguish physical page generation, pipe connection generation, and logical session generation. Do not reset backoff on a partial handshake.
9. Generic Runtime and Host code must not branch on a game name. Game-specific mapping belongs in a reusable profile/module plus a thin adapter.
10. Compile success alone is not acceptance. Tests, a real Player build, runtime logs, asset-fidelity checks, and manual Game View observations are separate evidence.

## Working rules

- Inspect `git status --short --branch` before editing and preserve unrelated user work.
- Use `rg`/`rg --files` for discovery and `apply_patch` for hand edits.
- Do not use Computer Use or GUI automation in this repository. Prefer CLI builds, test XML, process inspection, and runtime logs.
- Do not add or require hashes, checksums, file fingerprints, or duplicate integrity verification. A hash match must never gate startup, import, packaging, or bridge success.
- Do not add defensive branches for hypothetical failures, silent fallbacks, automatic repair, redundant retries, or parallel compatibility paths unless the requested behavior defines them. Follow the declared contract directly and surface a precise error when it is violated.
- Do not edit generated `dist/`, `dist-tests/`, `.NET bin/obj`, Unity `Library/Temp/Logs/Build`, or ignored integration workspaces as if they were source.
- Keep upstream game code and assets in their existing licensed boundary; game fixes stay separate from reusable bridge changes.
- Commit and push task-owned changes automatically after the required validation unless the user asks to leave them uncommitted or unpushed. Do not open a PR or rewrite history unless the user explicitly asks. Stage only paths owned by the current task.
- Never commit credentials, local machine paths, WebView profiles, crash dumps, packaged Players, or dependency directories.
- Keep TypeScript as ESM and compatible with the Node version in `package.json`.
- Keep Runtime C# compatible with the Unity version currently used by the validation project. Avoid Editor-only APIs in `unity-package/Runtime`.
- Use existing focused tests for the changed contract; add a test only when it verifies a concrete new behavior or reproduces the bug.

## Validation matched to the change

Choose from existing commands according to the affected component:

```powershell
npm test
npm run build
dotnet test .\webview-host-tests\ThreeUnityWebHost.Tests.csproj -c Release
```

Use the relevant test filter when available. Documentation-only edits need content and diff review. Do not run the whole Node/.NET/Unity matrix for every change.

For Unity package changes requiring sample resources, use `npm run samples:generate` and run the affected EditMode tests in the existing validation project. A zero-test XML is not a pass.

For Windows Player lifecycle or packaging changes, exercise the changed path through the real Player. Check the relevant page/bridge-ready and logic-tick markers, session isolation, or process cleanup as applicable. Confirm packaged web paths through loading, without hashes. Runtime logs, automated tests, and user visual/UI/input observations are separate evidence; report any unavailable Windows validation.

## Performance work

When the user asks for performance work, compare the affected workload before and after using an existing measurement or the smallest necessary benchmark. Preserve reliable-message ordering, session isolation, gameplay, and the original UI. Do not add a monitoring framework or benchmark matrix for ordinary feature work.

## Documentation and evidence

Update only documentation whose public behavior or verified facts changed. Keep generated conversions and reports under existing ignored local paths. Report actual commands/results and Windows/Unity limitations; do not reuse old test counts as current evidence.
