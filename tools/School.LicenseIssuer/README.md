# 供应商许可证签发工具

此工具由供应商独立保管和运行，不放入学校端安装包。学校端只配置验签公钥与已签发许可证。私钥与密码不得进入源码库、学校端发布目录、日志或交付文档。

工具使用 .NET 10 自带的加密 API，没有外部 NuGet 依赖。生成 RSA-3072 密钥，私钥采用密码加密的 PKCS#8 PEM（AES-256-CBC、PBKDF2-HMAC-SHA256，600000 次迭代）。签名为 RSA-SHA256、PKCS#1 v1.5，与 `OperationsEndpoints.cs` 的验签逻辑对应。

## 构建与帮助

```powershell
dotnet build tools/School.LicenseIssuer/School.LicenseIssuer.csproj -c Release
dotnet run --project tools/School.LicenseIssuer -- --help
```

可为供应商单独发布自带运行时的 Windows 工具：

```powershell
dotnet publish tools/School.LicenseIssuer/School.LicenseIssuer.csproj -c Release -r win-x64 --self-contained true -o .local/issuer-program
```

该目录是供应商工具，不是学校端安装载荷。不要把密钥放进工具项目或程序发布目录。

## 生成密钥

在供应商受控目录生成，程序拒绝覆盖同名密钥：

```powershell
dotnet run --project tools/School.LicenseIssuer -- keygen --out-dir "D:\SupplierSecrets\SchoolTraining"
```

交互方式隐藏输入私钥密码并再次确认。自动化调用可以通过环境变量 `SCHOOL_ISSUER_KEY_PASSWORD` 提供密码，运行结束后应移除该环境变量；不要将实际密码写入脚本、命令参数或提交到源码库。密码长度为 12–1024 个字符。

输出：

- `issuer-private.encrypted.pem`：仅供应商保存。Windows 上文件 ACL 限制为当前账户与 SYSTEM；其他平台创建为仅文件所有者可读写。
- `license-public.pem`：配置到学校端程序目录，或通过 `SCHOOL_LICENSE_PUBLIC_KEY` 指定路径。
- 控制台显示公钥 SHA-256 指纹，便于核对学校端的验签公钥。

## 签发

从学校端“许可证管理”页面复制设备特征码。资源文件为 JSON 数组，字段必须完整：

```json
[
  { "name": "实训节点", "authorizationCode": "TRAINING.NODES", "quantity": 2 }
]
```

平台额度使用 `TRAINING.CABINETS`、`TRAINING.DEVICES`、`TRAINING.NODES`、`TRAINING.VMS`、`TRAINING.ASSIGNMENTS`。每种代码在同一张许可证中只出现一次。节点账号按节点与账号的每一条关联计数。学校端对同类有效许可证采用最高额度，满足续期和额度调整需求。其他资源授权码仍可用于记录。

```powershell
dotnet run --project tools/School.LicenseIssuer -- issue `
  --private-key "D:\SupplierSecrets\SchoolTraining\issuer-private.encrypted.pem" `
  --machine-code "学校端显示的32位十六进制特征码" `
  --resources "D:\SupplierSecrets\orders\resources.json" `
  --output "D:\SupplierSecrets\issued\SCHOOL-2026-001.json" `
  --license-id "SCHOOL-2026-001" `
  --expires 2027-12-31
```

`--expires` 可省略，省略即无截止日。日期按北京时间当日结束解释，实际载荷存储 UTC 时间。已过期的日期、重复资源名称、负数数量、未知选项、明文私钥和已有输出文件均会被拒绝。资源数量为 0 时允许签发，与学校端校验规则一致。

签发后工具会用导出的公钥自行验签，再写入许可证。学校端还会检查产品、设备绑定、有效期和重复许可证编号。最终应在目标学校程序中实际导入验证；本工具不会替代学校端业务校验。

## 文件格式

外层 JSON：

```json
{ "payload": "UTF-8载荷JSON的Base64", "signature": "RSA-SHA256签名的Base64" }
```

载荷字段严格对应：

```json
{
  "licenseId": "SCHOOL-2026-001",
  "product": "SchoolTraining",
  "machineCode": "32位设备特征码",
  "issuedAt": "UTC时间",
  "expiresAt": null,
  "resources": [{ "name": "资源名称", "authorizationCode": "授权码", "quantity": 2 }]
}
```

签名覆盖解码后原始 UTF-8 载荷字节。不要对载荷重新排版后保留旧签名。许可证可读但不能在保留有效签名的情况下修改；Base64 不是加密。

## 返回值与验证

退出码：`0` 成功，`2` 参数或资源格式错误，`3` 密钥、权限或文件错误。错误不会打印私钥或密码。

独立检查脚本为 `verify-smoke.mjs`。使用 Node 的内置加密库核验加密私钥、签名、修改载荷后验签失败及有效期时区边界；测试密钥应创建在被忽略的临时目录中。

如需同时验证学校端实际导入，在全新隔离服务实例上设置 `SCHOOL_ISSUER_TEST_URL`、`SCHOOL_TEST_USER`、`SCHOOL_TEST_PASSWORD`、`SCHOOL_TEST_ALLOW_MUTATION=1`，并将 `SCHOOL_ISSUER_TEST_PUBLIC_KEY_PATH` 指向该临时程序目录内尚不存在的 `license-public.pem`。脚本只允许本机服务与工作区 `.local` 下的公钥夹具路径，验证导入成功、重复、篡改和错误设备绑定；结束后删除测试密钥及公钥夹具。测试实例的数据目录也必须独立，禁止用于学校正式环境。
