# API contract v0.1

All JSON properties camelCase. Base URL `/api`. Errors use `{message, errors?, traceId?}`: 400 for invalid input, 409 for unique/reference conflicts, 503 plus `Retry-After` when SQLite is busy, 507 when the backup disk lacks free space or free space cannot be determined, and 500 for unexpected failures. Lists use `{items:[],total:123}`; `?search=&page=1&pageSize=20`. Auth uses HttpOnly same-origin cookie. Mutating requests (except setup/login) send `X-CSRF-TOKEN` from `GET /api/auth/me` or login response `csrfToken`. No secrets in API list responses. `POST /api/auth/login` returns the same 401 and message for unknown accounts and wrong passwords, and performs one password-hash verification in both cases so response time does not reveal valid accounts.

Every endpoint binds the client disconnect signal (`HttpContext.RequestAborted`) to its database reads, writes, lock waits and file or backup I/O; a browser that cancels a request also stops the server-side work instead of leaving a query or an admin-lock waiter running. Single-row primary-key lookups may omit the token because they are bounded and immediate.

Frontend response contracts live in `web/src/types.ts`. `api<T>`, `post<T>` and `put<T>` have no `any` default, so each call site must name the expected payload; `Row` is reserved for runtime-configured entity forms and tables.

## Setup and session
- GET `/status`: `{initialized, product, version, schemaVersion, logWriteFailures, schoolName}` anonymous
- POST `/setup`: `{schoolName, userName, displayName,password}` -> session, first admin, loopback only
- POST `/auth/login`: `{userName,password}` -> `{user:{id,userName,displayName,role},csrfToken}`
- GET `/auth/me`: same session response
- POST `/auth/logout`
- POST `/auth/password`: `{currentPassword,newPassword}`
- GET `/dashboard`: `{schoolName,counts:{professions,classes,teachers,students,cabinets,devices,nodes},recentAudits:[],server:{...}}` actual data only

## Academics (admin)
- GET/POST `/professions`, PUT/DELETE `/professions/{id}`
  fields `{id,name,code,remark}`
- GET/POST `/classes`, PUT/DELETE `/classes/{id}`
  fields `{id,name,professionId,professionName,remark}`
- GET/POST `/people?kind=teacher|student`, PUT/DELETE `/people/{id}`
  fields `{id,kind,name,userName,number,classId,className,gender,phone,email,role}`
  create also accepts `password` (required >=12); roles `Admin|Teacher|Student`.
- POST `/people/batch-delete`: `{ids:[]}` atomic after reference checks
- POST `/people/{id}/reset-password`: `{newPassword}`
- POST `/imports/preview?kind=teacher|student`: multipart `file` (.xlsx/.csv)
  -> `{batchId,rows:[{rowNumber,data:{name,userName,number,className,gender,phone,email},errors:[{column,message}]}],validCount,errorCount}`
- POST `/imports/{batchId}/commit`: `{removeInvalidRows:true|false,initialPassword}` -> `202 {batchId,status,processedCount,totalCount,...}`. Password hashing runs in a bounded-concurrency background worker; the initial password stays in process memory and is never persisted. A restart fails unfinished batches safely.
- GET `/imports/{batchId}`: `{batchId,status,processedCount,totalCount,importedCount,removedCount,failureMessage,createdAt,queuedAt,startedAt,completedAt,message}`; `status` is `preview|queued|running|cancelling|completed|failed|cancelled`. Clients poll until a terminal state.
- POST `/imports/{batchId}/cancel`: request cooperative cancellation; completed batches return 409
- GET `/imports/template?kind=teacher|student`: CSV template

## Infrastructure (admin; agent implements)
- GET/POST `/device-types`, PUT/DELETE `/device-types/{id}`
  fields `{id,name,newline,backspace,remark}`
- GET/POST `/cabinets`, PUT/DELETE `/cabinets/{id}`
  fields `{id,name,host,port,jumpHost,jumpPort,jumpUserName,jumpPassword,remark}`
  jumpPassword write-only; responses `hasJumpPassword`.
- POST `/cabinets/test-connections`: `{ids:[]}` real timed probes -> `{items:[{id,success,message}]}`
- POST `/cabinets/{id}/reclaim`: release platform sessions/reservations; never pretend hardware reset
- GET/POST `/devices?cabinetId=...`, PUT/DELETE `/devices/{id}`
  fields `{id,name,cabinetId,deviceTypeId,host,port,connectionType,serialPortNumber,webPort,webProtocol,userName,password,remark}`
  password write-only; connectionType `ssh|telnet|tcp`; responses `hasPassword`.
- POST `/devices/{id}/connect-test`: real timed probe
- GET `/terminal/{deviceId}` WebSocket; validate same origin, admin cookie. Input/output raw terminal text; xterm UI. No credentials in URL.
- GET `/server/info`: actual management-host CPU/memory/network inventory and status
- GET/POST `/nodes`, PUT/DELETE `/nodes/{id}`
  fields `{id,name,host,provider,port,userName,password,enabled,remark}`; provider `hyperv|proxmox`; write-only secrets
- GET `/nodes/{id}/status`: live provider response or explicit unavailable/unsupported, never fabricated
- GET `/virtual-machines`: actual connected-provider list; no fabricated data

## Operations
- GET `/audits`: `{items:[{id,at,userName,action,target,detail}],total}` ordered by `at` then `id` descending so pages stay stable when timestamps repeat; backed by `IX_Audits_At_Id`. Records are retained until an operator archives or removes them.
- GET `/settings`: `{schoolName,backupDirectory,...}`; PUT `/settings`: admin
- POST `/backups`: create consistent local SQLite backup -> `{fileName,createdAt,size}`; returns 507 before writing when the free-space probe reports insufficient space or cannot read the volume
- GET `/backups`: list; GET `/backups/{fileName}` authorized file download
- GET `/licenses`: signed license metadata, resource counts, machine code (private keys never shipped)
- POST `/licenses`: multipart `file` for signed license upload; reject unsigned/invalid resources

## UI principles
Chinese UI, navy sidebar / crisp light workspace, accessible text, actual counts and empty states. No fake demo data, misleading connection states, or controls that silently do nothing. Show actionable API errors. Reusable searchable tables, form validation, explicit destructive confirmation. Sensitive inputs blank on edit means unchanged; never repopulate secrets.
