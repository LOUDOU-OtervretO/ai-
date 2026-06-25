# AI Diagnosis Server (Windows) — Prototype

这是一个 ASP.NET Core Minimal API 原型，用来接收 Windows 客户端上传的音频，调用 OpenAI Whisper 转写，再把转写与本机 diagnostics 发送到 OpenAI Chat Completion（要求返回严格 JSON），最后提供受限命令执行接口。

> 警告与隐私
> - 本服务会把脱敏后的诊断信息发送到 OpenAI，请确保在使用前告知并取得用户同意。
> - 执行命令功能只允许白名单模板，并有基本危险关键字检查；仍请谨慎使用。

运行要求
- .NET 7 SDK
- Windows 环境（部分 diagnostics 使用 PerformanceCounter）
- OpenAI API Key（设置环境变量 OPENAI_API_KEY）

配置与运行
1. 在 PowerShell / CMD 中设置环境变量 (临时示例)：

   - PowerShell（当前会话）：
     ```powershell
     $env:OPENAI_API_KEY = "sk-..."
     ```

   - Windows 环境变量（永久）：通过 系统设置 → 高级系统设置 → 环境变量 添加 OPENAI_API_KEY

2. 可选环境变量：
   - OPENAI_API_BASE (默认 https://api.openai.com)
   - OPENAI_MODEL (默认 gpt-4)
   - EXEC_CONFIRM_TOKEN (默认 confirm-token-sample，强烈建议在生产更换为安全随机串)

3. 运行服务：

   ```bash
   dotnet run
   ```

   默认监听： http://localhost:5000

API 说明（快速测试）

- POST /api/uploadAudio
  - 内容：multipart/form-data，字段名 `audio`（WAV 文件）
  - 返回示例：{ "transcript": "...", "transcription_engine": "openai-whisper", "file":"/path/to/upload.wav" }

- GET /api/diagnostics
  - 返回本机诊断快照（CPU、内存、磁盘、top processes）

- POST /api/analyze
  - 请求 JSON：{ "transcript": "...", "diagnostics": { ... } }
  - 响应：LLM 返回的严格 JSON（issue_summary、probable_causes、recommended_actions 等）

- POST /api/execute
  - 请求 JSON：{ "actionId": "A1", "confirmToken": "...", "parameters": {"pid":"1234"} }
  - 返回：{ status: "ok|denied|error", stdout: "...", stderr: "...", exit_code: 0 }

安全与部署注意事项（必须在生产前评估）

- 不要把 OPENAI_API_KEY 写在源码中；使用系统环境变量或受管密钥库（Azure Key Vault、AWS Secrets Manager 等）。
- 请把 EXEC_CONFIRM_TOKEN 替换为强随机值或使用更安全的认证方式（Windows 身份验证、mTLS、OAuth）。
- 强烈建议在生产环境开启 HTTPS、限制访问范围（只允许 localhost 或受信任主机），并启用详细审计日志（谁、何时、执行了什么）。
- 对 LLM 返回做严格 JSON Schema 校验并对命令执行做白名单/模板校验；目前实现为基础示例，不够完备。
- 在发送诊断数据到 OpenAI 前应先在客户端征得用户同意并尽量脱敏：当前代码包含示例脱敏（IP、邮箱、Windows 路径），但可能不足以满足特定法规要求。
- 生产环境建议：将 actionsStore 持久化（例如 SQLite），并对用户与管理员进行权限分级与审批流程。

改进建议 / 后续工作
- 添加或集成本地 STT fallback（如 whisper.cpp）以降低成本或提高隐私。 
- 使用 JsonSchema 进行 LLM 输出校验，必要时对模型输出进行修正/重试。 
- 实现客户端（WPF）完整交互：录音 → 上传 → 展示分析 → 选择 action → 二次确认 → 执行。 
- 为 execute 加入多重确认（UI 确认 + UAC 提升）并保存审计日志到安全存储。
- 将服务打包为 Windows Service（可使用 sc.exe 或 NSSM）并提供安装脚本。

测试示例（curl 快速验证）

1) 上传音频（假设有 test.wav）：

```bash
curl -X POST -F "audio=@test.wav" http://localhost:5000/api/uploadAudio
```

2) 获取 diagnostics：

```bash
curl http://localhost:5000/api/diagnostics
```

3) 分析（示例，需替换 transcript 与 diagnostics）：

```bash
curl -X POST -H "Content-Type: application/json" -d '{"transcript":"我的电脑很卡","diagnostics":{}}' http://localhost:5000/api/analyze
```

4) 执行（示例 actionId = A1）：

```bash
curl -X POST -H "Content-Type: application/json" -d '{"actionId":"A1","confirmToken":"confirm-token-sample","parameters":{"pid":"1234"}}' http://localhost:5000/api/execute
```

常见故障排查
- 如果转写出现 401/403，确认 OPENAI_API_KEY 是否正确并未过期。 
- 如果 diagnostics 返回空或 -1，确认 PerformanceCounter 在当前环境下是否可用（某些 Windows 容器环境可能受限）。
- 如果 LLM 返回非 JSON，请检查 OPENAI_MODEL 是否可用或降低模型 temperature 到 0.0 以提高确定性。

许可与免责声明
- 本项目为原型示例，仅供开发/测试使用。请在生产部署前做安全评估、代码审计与合规审查。

