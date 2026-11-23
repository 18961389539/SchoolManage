# API contract v0.1

All JSON properties camelCase. Base URL `/api`. Errors use `{message, errors?, traceId?}`: 400 for invalid input, 409 for unique/reference conflicts, 503 plus `Retry-After` when SQLite is busy, 507 when the backup disk lacks free space or free space cannot be determined, and 500 for unexpected failures. Lists use `{items:[],total:123}`; `?search=&page=1&pageSize=20`. Auth uses HttpOnly same-origin cookie. Mutating requests (except setup/login) send `X-CSRF-TOKEN` from `GET /api/auth/me` or login response `csrfToken`. No secrets in API list responses. `POST /api/auth/login` returns the same 401 and message for unknown accounts and wrong passwords, and performs one password-hash verification in both cases so response time does not reveal valid accounts.

Every endpoint binds the client disconnect signal (`HttpContext.RequestAborted`) to its database reads, writes, lock waits and file or backup I/O; a browser that cancels a request also stops the server-side work instead of leaving a query or an admin-lock waiter running. Single-row primary-key lookups may omit the token because they are bounded and immediate.

The web client gives a request 60 s — 10 min for uploads and file downloads — and reports a timeout instead of spinning forever; operations the server may legitimately spend minutes on (backup creation, Hyper-V power) opt out explicitly. Changing page cancels that page's in-flight reads and never its writes or a download already started, so a form save, an exam answer or an export already on the wire still lands.

Import preview/commit, backup creation/download, diagnostics, statistics exports and license imports are limited to 60 requests per minute per account; exceeding the window returns 429.

Frontend response contracts live in `web/src/types.ts`. `api<T>`, `post<T>` and `put<T>` have no `any` default, so each call site must name the expected payload; `Row` is reserved for runtime-configured entity forms and tables. The import dialog additionally checks the preview and status payload shapes at runtime, so a renamed field becomes a visible contract error instead of blank cells.

## Setup and session
- GET `/status`: `{initialized, product, version, schemaVersion, logWriteFailures, maxUploadBytes, schoolName}` anonymous. `maxUploadBytes` is the single source for the upload limit the web client enforces.
- GET `/healthz`: anonymous `{status:"healthy"|"unhealthy", checks:{database:"ok"|"failed", disk:"ok"|"low"|"unavailable"}}`; 200 when healthy, 503 otherwise. No paths, accounts or version are returned.
- Session cookies are HttpOnly, SameSite=Strict, 8 h sliding with a 24 h absolute limit; `School:Session:AbsoluteHours` adjusts the absolute window (1–168 h).
- POST `/setup`: `{schoolName, userName, displayName,password}` -> session, first admin, loopback only
- POST `/auth/login`: `{userName,password}` -> `{user:{id,userName,displayName,role},csrfToken}`
- GET `/auth/me`: same session response
- POST `/auth/logout`
- POST `/auth/password`: `{currentPassword,newPassword}`
- GET `/dashboard`: `{schoolName,counts:{professions,classes,teachers,students,cabinets,devices,nodes},recentAudits:[],server:{...}}` actual data only. Aggregate counts are readable by any signed-in account; `recentAudits` is empty unless the caller is an administrator.

## Academics (admin)
- GET/POST `/professions`, PUT/DELETE `/professions/{id}`
  fields `{id,name,code,remark}`
- GET/POST `/classes`, PUT/DELETE `/classes/{id}`
  fields `{id,name,professionId,professionName,remark}`. The GET list combines `?professionId=...` with the name or profession keyword search.
- GET/POST `/people?kind=teacher|student`, PUT/DELETE `/people/{id}`
  fields `{id,kind,name,userName,number,classId,className,gender,phone,email,role}`
  create also accepts `password` (required >=12); roles `Admin|Teacher|Student`. The GET list combines `classId`, `role` and `search` with `kind`. Search matches name, account, staff or student number, and phone. Teacher management exposes the `Admin|Teacher` role choices; student management exposes the class filter.
- POST `/people/batch-delete`: `{ids:[]}` atomic after reference checks
- POST `/people/{id}/reset-password`: `{newPassword}`
- POST `/imports/preview?kind=teacher|student`: multipart `file` (.xlsx/.csv)
  -> `{batchId,rows:[{rowNumber,data:{name,userName,number,className,gender,phone,email},errors:[{column,message}]}],validCount,errorCount}`
- POST `/imports/{batchId}/commit`: `{removeInvalidRows:true|false,initialPassword}` -> `202 {batchId,status,processedCount,totalCount,...}`. Password hashing runs in a bounded-concurrency background worker; the initial password stays in process memory and is never persisted. A restart fails unfinished batches safely.
- GET `/imports/{batchId}`: `{batchId,status,processedCount,totalCount,importedCount,removedCount,failureMessage,createdAt,queuedAt,startedAt,completedAt,message}`; `status` is `preview|queued|running|cancelling|completed|failed|cancelled`. Clients poll until a terminal state.
- POST `/imports/{batchId}/cancel`: request cooperative cancellation; completed batches return 409
- GET `/imports/template?kind=teacher|student`: CSV template

## Infrastructure
- GET/POST `/device-types`, PUT/DELETE `/device-types/{id}`
  fields `{id,name,newline,backspace,remark}`
- GET/POST `/cabinets`, PUT/DELETE `/cabinets/{id}`
  fields `{id,name,host,port,jumpHost,jumpPort,jumpUserName,jumpPassword,remark}`
  jumpPassword write-only; responses `hasJumpPassword`.
- POST `/cabinets/test-connections`: `{ids:[]}` -> `{items:[{id,name,success,message,isSimulated,level}]}`. Results are ordered by cabinet name and ID. Writes an audit entry. The simulator reports an in-process response. External probes report timed TCP reachability only, including jump host reachability when configured. The outbound target policy applies to external probes.
- POST `/cabinets/{id}/reclaim`: closes current platform terminal sessions and releases their leases. Saved simulator configuration is retained.
- GET/POST `/devices?cabinetId=...`, PUT/DELETE `/devices/{id}`
  fields `{id,name,cabinetId,deviceTypeId,nodeId,host,port,connectionType,serialPortNumber,webPort,webProtocol,userName,password,remark}`
  password write-only; connectionType `simulated|ssh|telnet|tcp`; responses include `hasPassword`. Administrators may link a simulated device to a simulated node via nullable `nodeId`. The v5 migration associates built-in simulated devices, and operators can link other simulated devices explicitly.
  List responses include `cabinet: {id,name}` when a matching cabinet scope is supplied, even if the device list is empty. Other requests return `cabinet: null`.
- POST `/devices/{id}/connect-test`: returns simulated connectivity for built-in devices. External direct probes are subject to the same outbound policy as cabinet probes. A real device behind a jump host returns an unsupported result.
- GET `/devices/{id}/web-console`: simulated devices only, with the same device scope as the terminal. Returns `{id,name,isSimulated,hostname,interfaces:[{name,enabled,description}],enabledInterfaceCount,busy,hasStartupConfiguration,startupHostname,unsavedChanges,version}`. Busy devices remain readable.
- PUT `/devices/{id}/web-console`: `{version,hostname,interfaces:[{name,enabled,description}]}` updates the simulated running configuration. PUT `/devices/{id}/web-console/startup`: `{version}` saves the running configuration as startup configuration. Both return the updated snapshot and reject active sessions or stale versions with 409. The Web console and CLI use the same settings. Unknown and out-of-scope devices return 404.
- GET `/terminal/{deviceId}` WebSocket; validates same origin and the caller's current session. Administrators may manage all supported devices. Teachers and students can open simulated devices linked to their active assigned node. The server rechecks authorization during the lease. External terminal targets use the same outbound target policy.
- GET `/my-resources`: `{nodes,devices,virtualMachines}` scoped to the signed-in teacher's or student's assigned simulated nodes. Disabled nodes are listed without terminal or Web write access. VM power controls remain administrator-only.
- GET `/server/info`: actual management-host CPU/memory/network inventory and status
- GET/POST `/nodes`, PUT/DELETE `/nodes/{id}`
  The paginated GET list combines `provider=simulated|hyperv|proxmox` with node name/address search. Each row includes `enabled`, `healthStatus`, `cpuUsagePercent`, `memoryUsagePercent`, `diskUsagePercent`, `virtualMachineCount`, `virtualMachineQuota`, `accountCount` and `accountQuota`. Simulated health is `healthy` independently of scheduling status. The four metric fields are `null` for unconnected providers. Provider credentials are write-only.
- GET `/nodes/{id}/status`: simulated node details use the same resource calculations as the list. CPU and memory percentages measure running VM reservations, and disk counts all VM reservations. Unconnected provider details return 501.
- POST `/nodes/{id}/enable` and `/nodes/{id}/disable`: change simulated node scheduling eligibility. A disabled node blocks new resource assignments and VM starts. Its running VMs retain their current state.
- GET/PUT `/nodes/{id}/configuration`: simulated node IPv4 address, subnet mask, gateway and clock. PUT accepts `{ipAddress,subnetMask,gateway,systemTime?}`, with a timezone-qualified ISO timestamp for clock changes. The Windows management host remains read-only.
- GET/POST `/virtual-machines`, PUT/DELETE `/virtual-machines/{id}`: the list is paginated and supports name search and `nodeId` filtering. Administrators can create and edit simulated virtual machines with `{name,nodeId,cpuCount,memoryMb,diskGb,remark}`. POST `/virtual-machines/{id}/start` and `/virtual-machines/{id}/stop` change simulation state. Edits and deletion require a stopped VM.

## Operations
- GET `/audits`: `{items:[{id,at,userName,action,target,detail}],total}` ordered by `at` then `id` descending so pages stay stable when timestamps repeat; backed by `IX_Audits_At_Id`. Records are kept by default; `School:Audits:RetentionDays` enables the scheduled cleanup described in the operations guide.
- GET `/audits/export?search=...`: streaming UTF-8 CSV with the same search filter, ordered oldest first, timestamps in Beijing time. Cells starting with `= + - @` are prefixed with `'` so spreadsheet software does not evaluate them. The export itself is audited.
- GET `/diagnostics`: administrator-only support ZIP with `README.txt`, `status.json`, `settings.json`, `system.json`, `database.json`, the tail of the three most recent service logs and `manifest.json`. It contains no passwords, cookies, DataProtection keys or database file, and each download is audited.
- GET `/settings`: `{schoolName,backupDirectory,...}`; PUT `/settings`: admin
- POST `/backups`: create consistent local SQLite backup -> `{fileName,createdAt,size}`; returns 507 before writing when the free-space probe reports insufficient space or cannot read the volume, and 409 when another backup is already running
- GET `/backups`: list plus `automatic:{enabled,timeOfDay,lastAttemptAt,lastSuccessAt,lastMessage}`; the scheduled worker runs daily at the configured local time, catches up when no backup succeeded in the last 24 h, and retries failures hourly
- GET `/backups/{fileName}`: authorized file download
- Backups default to `<data>/backups`; set `School:Backups:Directory` to an absolute path on another volume to separate them from the database. The ZIP contains the database, DataProtection keys and decryptable device credentials and must be handled as credential material.
- GET `/licenses`: signed license metadata, machine code and five quota rows `{code,name,used,limit}`. `limit:null` means the dimension has never been enabled. `limit:0` forbids new allocation for that dimension.
- POST `/licenses`: multipart `file` accepts 1–20 signed files, atomically. Resource codes `TRAINING.CABINETS`, `TRAINING.DEVICES`, `TRAINING.NODES`, `TRAINING.VMS` and `TRAINING.ASSIGNMENTS` enable corresponding limits. The highest currently valid grant determines the cap. Expired, corrupted or unverifiable grants give no capacity. Administrators can still manage and remove existing records. Legacy records remain readable and can be re-uploaded with the original signature to activate limits.
- GET `/licenses/guide`: download usage and quota code instructions.

## Editing conflicts (course content)
- GET `/courses`, `/courses/{id}` and `/resources/{id}` return `version` for the course, every chapter/section/resource node and the resource detail.
- PUT `/courses/{id}`, `/chapters/{id}`, `/sections/{id}` and `/resources/{id}` require the `version` the client loaded. A missing value is 400; a value that no longer matches — or a save that loses the race at the storage layer — is 409 `{message, code:"stale"}`. A successful write increments `version` by one.
- The web client reads `code:"stale"` as "someone else saved first" and reloads the server copy instead of overwriting it; any other 409 keeps its business meaning (duplicate name, resource already in use).

## UI principles
Chinese UI, navy sidebar / crisp light workspace, accessible text, actual counts and empty states. No fake demo data, misleading connection states, or controls that silently do nothing. Show actionable API errors. Reusable searchable tables, form validation, explicit destructive confirmation. Sensitive inputs blank on edit means unchanged; never repopulate secrets.
