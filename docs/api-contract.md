# API contract v0.1

All JSON properties camelCase. Base URL `/api`. Errors use `{message, errors?, traceId?}`: 400 for invalid input, 409 for unique/reference conflicts, 503 plus `Retry-After` when SQLite is busy, 507 when the backup disk lacks free space or free space cannot be determined, and 500 for unexpected failures. Lists use `{items:[],total:123}`; `?search=&page=1&pageSize=20`. Auth uses HttpOnly same-origin cookie. Mutating requests (except setup/login) send `X-CSRF-TOKEN` from `GET /api/auth/me` or login response `csrfToken`. No secrets in API list responses. `POST /api/auth/login` returns the same 401 and message for unknown accounts and wrong passwords, and performs one password-hash verification in both cases so response time does not reveal valid accounts.

Every endpoint binds the client disconnect signal (`HttpContext.RequestAborted`) to its database reads, writes, lock waits and file or backup I/O; a browser that cancels a request also stops the server-side work instead of leaving a query or an admin-lock waiter running. Single-row primary-key lookups may omit the token because they are bounded and immediate.

The web client gives a request 60 s — 10 min for uploads and file downloads — and reports a timeout instead of spinning forever; operations the server may legitimately spend minutes on (backup creation, Hyper-V power) opt out explicitly. Changing page cancels that page's in-flight reads and never its writes or a download already started, so a form save, an exam answer or an export already on the wire still lands.

Import preview/commit, backup creation/download, diagnostics, statistics exports and license imports are limited to 60 requests per minute per account; exceeding the window returns 429. Login attempts are limited to 120 per minute per client IP (`School:Login:IpPermitLimit`). Eight consecutive password failures for the same account from that client return 429 with `Retry-After` and lock the pair for 5 minutes (`School:Login:FailureLimit`, `School:Login:LockoutMinutes`). A successful login, or a gap longer than the lockout between failures, clears the streak before it locks. Other accounts behind the same NAT are not locked. The client IP is the TCP peer unless that peer is listed in `School:ReverseProxy:TrustedProxies`, in which case one `X-Forwarded-For` hop is used. `X-Forwarded-Proto` and `X-Forwarded-Host` from those peers are applied before the origin check. Peers that are not listed cannot supply these headers. First-run setup still authorizes only the TCP peer, never the forwarded client address.

Session, status, and exam-question read responses are named records in the OpenAPI document `contract`. `npm run check:api` exports that document and fails when `web/src/api.generated.ts` drifts; `npm run check:api -- --write` regenerates it. The rest of `web/src/types.ts` is still maintained by hand. `api<T>`, `post<T>` and `put<T>` have no `any` default, so each call site must name the expected payload; `Row` is reserved for runtime-configured entity forms and tables. The import dialog additionally checks the preview and status payload shapes at runtime, so a renamed field becomes a visible contract error instead of blank cells.

## Setup and session
- GET `/status`: `{initialized, product, version, schemaVersion, logWriteFailures, maxUploadBytes, schoolName}` anonymous. `maxUploadBytes` is the single source for the upload limit the web client enforces.
- GET `/healthz`: anonymous `{status:"healthy"|"unhealthy", checks:{database:"ok"|"failed", disk:"ok"|"low"|"unavailable"}}`; 200 when healthy, 503 otherwise. No paths, accounts or version are returned.
- Session cookies are HttpOnly, SameSite=Strict, 8 h sliding with a 24 h absolute limit; `School:Session:AbsoluteHours` adjusts the absolute window (1–168 h). Each API request checks the account security stamp. A matching stamp is remembered for 5 seconds. Logout, a password change, an administrator password reset, an account or role rename, and account deletion drop that memory immediately.
- POST `/setup`: `{schoolName, userName, displayName,password}` -> session, first admin, TCP peer must be loopback or the server's own address. `X-Forwarded-For` is ignored for this check.
- POST `/auth/login`: `{userName,password}` -> `{user:{id,userName,displayName,role},csrfToken}`. 429 `{message:"该账号登录失败次数过多，请稍后再试"}` plus `Retry-After` after 8 consecutive failures for that client and account. 429 `{message:"操作过于频繁，请稍后重试"}` when the per-client window (120/min) is full.
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
- POST `/imports/preview?kind=teacher|student&pageSize=50`: multipart `file` (.xlsx/.csv)
  -> `{batchId,rows:[{rowNumber,data:{name,userName,number,className,gender,phone,email},errors:[{column,message}]}],validCount,errorCount}`. `rows` is the first page (`pageSize` 1–200, default 50). `validCount` and `errorCount` cover the whole file. Preview lines are stored per row.
- GET `/imports/{batchId}/rows?page=1&pageSize=50&filter=all|invalid|valid`: `{rows,totalCount,page,pageSize}` for the preview table. `filter=invalid` is problem rows; `filter=valid` is rows that will be imported.
- POST `/imports/{batchId}/commit`: `{removeInvalidRows:true|false}` -> `202 {batchId,status,processedCount,totalCount,...}`. The worker generates a distinct initial password for every account and keeps only the one-time receipt in process memory; plaintext passwords are never persisted. A restart fails unfinished batches safely.
- GET `/imports/{batchId}`: `{batchId,status,processedCount,totalCount,importedCount,removedCount,failureMessage,createdAt,queuedAt,startedAt,completedAt,message}`; `status` is `preview|queued|running|cancelling|completed|failed|cancelled`. Clients poll until a terminal state.
- GET `/imports/{batchId}/receipt`: after completion returns `{items:[{account,password}]}` once to the submitting administrator within 24 hours; later calls return 409. Passwords must be handed over securely and changed at first login.
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
- GET `/nodes/{id}/status`: simulated node details use the same resource calculations as the list. CPU and memory percentages measure running VM reservations, and disk counts all VM reservations. Unconnected provider details return 501. A configured Hyper-V node returns the latest snapshot: `collectedAt` is when that snapshot finished, and `refreshing` is true while a newer sync is running. A sync that has not finished yet returns null metrics and `refreshing: true`. A finished sync with no metrics returns 503.
- POST `/nodes/{id}/enable` and `/nodes/{id}/disable`: change simulated node scheduling eligibility. A disabled node blocks new resource assignments and VM starts. Its running VMs retain their current state.
- GET/PUT `/nodes/{id}/configuration`: simulated node IPv4 address, subnet mask, gateway and clock. PUT accepts `{ipAddress,subnetMask,gateway,systemTime?}`, with a timezone-qualified ISO timestamp for clock changes. The Windows management host remains read-only.
- GET/POST `/virtual-machines`, PUT/DELETE `/virtual-machines/{id}`: the list is paginated and supports name search and `nodeId` filtering. Administrators can create and edit simulated virtual machines with `{name,nodeId,cpuCount,memoryMb,diskGb,remark}`. POST `/virtual-machines/{id}/start` and `/virtual-machines/{id}/stop` change simulation state. Edits and deletion require a stopped VM.
- GET `/hyperv/virtual-machines`: the same list shape for Hyper-V, read from the latest snapshot. `syncedAt` is the newest completed sync, `refreshing` is true while any listed node is syncing, `pendingNodes` have not completed a sync, and `unavailableNodes` failed their last sync or are outside `School:HyperV:Hosts`. Opening the list does not wait for PowerShell. At most four inspections run at once. Power actions and the RDP download still wait for the host; a download uses the snapshot and waits only when that host has never synced.

## Operations
- GET `/audits`: `{items:[{id,at,userName,action,target,detail}],total}` ordered by `at` then `id` descending so pages stay stable when timestamps repeat; backed by `IX_Audits_At_Id`. Records are kept by default; `School:Audits:RetentionDays` enables the scheduled cleanup described in the operations guide.
- GET `/audits/export?search=...`: streaming UTF-8 CSV with the same search filter, ordered oldest first, timestamps in Beijing time. Cells starting with `= + - @` are prefixed with `'` so spreadsheet software does not evaluate them. The export itself is audited.
- GET `/diagnostics`: administrator-only support ZIP with `README.txt`, `status.json`, `settings.json`, `system.json`, `database.json`, the tail of the three most recent service logs and `manifest.json`. It contains no passwords, cookies, DataProtection keys or database file, and each download is audited.
- GET `/settings`: `{schoolName,backupDirectory,...}`; PUT `/settings`: admin
- POST `/backups`: create consistent local SQLite backup -> `{fileName,createdAt,size}`; returns 507 before writing when the free-space probe reports insufficient space or cannot read the volume, and 409 when another backup is already running
- GET `/backups`: list plus `automatic:{enabled,timeOfDay,lastAttemptAt,lastSuccessAt,lastMessage}`; the scheduled worker runs daily at the configured local time, catches up when no backup succeeded in the last 24 h, and retries failures hourly
- GET `/backups/{fileName}`: authorized file download
- POST `/backups/inspect`: multipart field `file`, a backup ZIP of at most 16MB. POST `/backups/{fileName}/inspect` checks a ZIP already in the backup directory. Both are read-only and return `{ok,message,schemaVersion,keyProtection,portable,credentialCount,warnings}`. `ok` is false when the database or keys fail the same checks as `--validate-backup`. `portable` is false while keys are still DPAPI-bound to the Windows install that created them.
- Backups default to `<data>/backups`; set `School:Backups:Directory` to an absolute path on another volume to separate them from the database. The ZIP contains the database, DataProtection keys and decryptable device credentials and must be handled as credential material. Set either `School:DataProtection:CertificatePath` plus `CertificatePassword`, or `School:DataProtection:RecoveryPassphrase`, then restart and take a new backup before the keys can be opened on another machine. The passphrase is not stored in the ZIP. `GET /backups` includes `keyProtection:{mode,portable,summary}`.
- GET `/licenses`: signed license metadata, machine code and five quota rows `{code,name,used,limit}`. `limit:null` means the dimension has never been enabled. `limit:0` forbids new allocation for that dimension.
- POST `/licenses`: multipart `file` accepts 1–20 signed files, atomically. Resource codes `TRAINING.CABINETS`, `TRAINING.DEVICES`, `TRAINING.NODES`, `TRAINING.VMS` and `TRAINING.ASSIGNMENTS` enable corresponding limits. The highest currently valid grant determines the cap. Expired, corrupted or unverifiable grants give no capacity. Administrators can still manage and remove existing records. Legacy records remain readable and can be re-uploaded with the original signature to activate limits.
- GET `/licenses/guide`: download usage and quota code instructions.

## Answer autosave
- POST `/exam-hall/{id}/answers` and POST `/camp/positions/{id}/answers` accept the questions changed since the last save. Questions omitted from the body stay as stored. A question whose selection and text already match the stored answer is not written again, and its answered time stays put. `saved` is how many rows were inserted or updated. Submit still accepts the whole paper.

## Camp assessments
- A student may take the same position more than once. Only one attempt per student and position may be `inProgress`; that limit is `IX_JobAttempts_PositionId_UserId_Active`.
- Starting an attempt records `ContentVersion` and `StartupNodeId`. Later edits to skills, questions, duration or the startup node apply to attempts started afterward. Grading, review and the ability report read the attempt's version.
- GET `/camp/attempts/{attemptId}/report` includes `attemptNumber` and `history`, the graded attempts for that student and position in start order, each with its own radar.

## Teaching content ownership
- Courses, exam question banks and exam papers carry `ownerUserId` and `visibility` (`private`, `shared`, `published`). `visibility` is separate from a course's draft/published `status`. Existing rows stay `published` with a null owner, which is the admin standard library.
- A teacher can create resources they own. New teacher resources start `private`. The owner may switch an unlisted resource between `private` and `shared`. Only an administrator can set `visibility` to `published`, and after that only an administrator can edit it.
- Teachers see the standard library, shared resources, and their own private resources. Students see published courses only, and still open course content through an enrolled classroom. A classroom can use an active standard-library course, a shared course that has been published, or the teacher's own published private course.
- Question and paper writes follow the same rule. A paper can include questions from any bank the caller can see. Names stay unique across the whole catalog.

## Editing conflicts (course content)
- GET `/courses`, `/courses/{id}` and `/resources/{id}` return `version` for the course, every chapter/section/resource node and the resource detail.
- PUT `/courses/{id}`, `/chapters/{id}`, `/sections/{id}` and `/resources/{id}` require the `version` the client loaded. A missing value is 400; a value that no longer matches — or a save that loses the race at the storage layer — is 409 `{message, code:"stale"}`. A successful write increments `version` by one.
- The web client reads `code:"stale"` as "someone else saved first" and reloads the server copy instead of overwriting it; any other 409 keeps its business meaning (duplicate name, resource already in use).

## Classroom live share
- `GET /classrooms/case-sessions/{sessionId}/live/socket` upgrades to a WebSocket. The cookie session is checked once on the handshake. Students publish; the classroom teacher and administrators watch.
- The student sends one text frame `{"allowAssist":true|false}` after the socket opens. Capture bytes are sent only after the server writes `{"type":"start"}`, which happens when at least one teacher is watching, and pause after `{"type":"stop"}` when the last watcher leaves.
- Frames are binary JPEG (SOI/EOI, at most 160000 bytes), not Base64 JSON. A watcher receives `{"type":"status","sharing":bool,"allowAssist":bool}` and then each JPEG. `allowAssist` on `GET /classrooms/case-sessions/{id}/work` stays true while the student socket is open.

## UI principles
Chinese UI, navy sidebar / crisp light workspace, accessible text, actual counts and empty states. No fake demo data, misleading connection states, or controls that silently do nothing. Show actionable API errors. Reusable searchable tables, form validation, explicit destructive confirmation. Sensitive inputs blank on edit means unchanged; never repopulate secrets.
