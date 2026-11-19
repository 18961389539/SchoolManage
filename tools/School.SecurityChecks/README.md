# 安全与停服不变量检查

此开发检查工具只在进程内运行假 `IHostApplicationLifetime`，不启动 Web/Windows 服务，不监听端口，不修改系统配置。通过 ProjectReference 引用主程序，并使用 ASP.NET Core FrameworkReference，没有额外 NuGet 测试框架。

```powershell
dotnet build tools/School.SecurityChecks/School.SecurityChecks.csproj -c Release
dotnet run --project tools/School.SecurityChecks -c Release --no-build
```

结果写入 `docs/verification/security-invariants.json`，包括被测主程序集 SHA-256。

检查请求取消隔离、ApplicationStopping 取消全部活动终端、停服后拒绝新租约、旧租约释放不影响后继租约、用户撤销/机柜回收隔离，以及并发建连与停服竞态。检查工具不会宣称已经验证 Windows SCM 的实际停服时长。

通过直接引用主程序的 `SetupAccess.IsLocalEndpoint(IPAddress?, IPAddress?)` 始终运行本机端点边界检查，方法缺失时不能编译，不会跳过。发布命令可显式保留强制标记：

```powershell
dotnet run --project tools/School.SecurityChecks -c Release --no-build -- --require-setup-access
```

16 个端点用例覆盖 loopback、本机 LAN 地址、IPv4-mapped IPv6、不同远端 LAN/IPv6、未知地址与 Any 地址。`--output <文件路径>` 可以指定单独的结果文件。
