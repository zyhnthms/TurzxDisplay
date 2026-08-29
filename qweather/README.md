# qweather — 和风天气密钥目录

此目录存放**和风天气** JWT 身份认证的 Ed25519 密钥，属于个人凭证，**不会也不应提交到仓库**（见根目录 `.gitignore`）。

## 如何生成

在和风天气控制台创建 JWT 凭据时（或本地 OpenSSL）：

```bash
openssl genpkey -algorithm ED25519 -out ed25519-private.pem
openssl pkey -pubout -in ed25519-private.pem > ed25519-public.pem
```

然后把 `ed25519-public.pem` 的内容上传到 [和风天气控制台 - 项目管理] 的凭据公钥框。

## 应用如何使用

- 应用依次从「exe 目录 → 项目根目录 → 工作目录」的 `qweather/ed25519-private.pem` 读取私钥
- 另需在应用「天气 → 和风天气」面板中填写：API Host、开发者ID、项目ID、凭据ID
- 详见主 README 的天气模式说明
