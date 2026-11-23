# 管理界面

Vue 3 / TypeScript / Element Plus。前端只在开发与构建时需要 Node；交付目录 `dist/` 是可离线托管的静态文件，不使用 CDN 或在线字体。

```powershell
npm.cmd ci
npm.cmd run build
```

开发代理默认指向 `http://127.0.0.1:5080`，启动开发服务器使用 `npm.cmd run dev`。发布时由外层构建脚本将 `dist/` 复制到 ASP.NET Core 的 `wwwroot/`。

界面与 `/api` 同源，登录状态使用 HttpOnly Cookie；变更请求携带当前会话返回的 CSRF token。终端使用同源 WebSocket，不在 URL 中传递设备凭据。

已实现的界面：本机初始化、登录与首次改密、控制台、专业班级和师生管理、导入校验、设备类型、机柜和设备、模拟终端、集群节点与模拟系统配置、模拟虚拟机、主机信息、审计、备份、许可证和系统设置。模拟资源有明确标识，统计和列表通过后端读取。

构建验证：`vue-tsc --noEmit` 和 Vite 生产构建。完整交付仍需与后端一起验证初始化、写操作、导入、WebSocket、服务重启和恢复流程；单独的前端构建不作为完整验收证据。

文件体积：新文件上限 800 行；`file-size-baseline.json` 记录存量超标文件的当前行数，这些文件只允许缩减，不允许继续增长。检查命令为 `npm.cmd run check:sizes`，发布门禁会执行；拆分或删除后运行 `npm.cmd run check:sizes -- --update` 下调基线。
