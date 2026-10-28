# LifeOS-AI

LifeOS is a personal workspace for keeping everyday plans in one place. It includes account registration and sign-in, tasks, finance, fitness, document storage, career tracking, and an optional AI assistant.

## Run locally

Requirements: .NET 10 SDK and the Microsoft Edge WebView2 Runtime (usually installed with Windows 11 and Microsoft Edge). The frontend development server additionally requires Node.js and npm.

Run the desktop app from the repository root:

```powershell
dotnet run --project backend/src/LifeOS.Desktop/LifeOS.Desktop.csproj
```

This opens LifeOS in a desktop window and loads the backend DLL in the desktop process. The UI calls the DLL through the WebView2 native message bridge. Desktop mode opens no API server, network listener, port, or localhost URL. The first launch applies EF Core migrations. The database and uploaded files are stored under `%LOCALAPPDATA%\LifeOS`; existing database and document files in `backend/src/LifeOS.Api` are copied there on first launch. Create an account in the sign-in page if this is your first time.

To run only the API for browser development, use `dotnet run --project backend/src/LifeOS.Api/LifeOS.Api.csproj` and open the local URL printed in the terminal.

The frontend source lives in `frontend/` and is bundled into the desktop output under `wwwroot`. For browser-based frontend development, the optional standalone API can be started with `dotnet run --project backend/src/LifeOS.Api/LifeOS.Api.csproj --launch-profile http`; then run `npm install` and `npm run dev` from `frontend/`. Open the Vite URL shown in the terminal. This standalone development mode is separate from desktop mode.

Create an account in the desktop window. Passwords must be at least 10 characters. Sign-in issues a bearer session that expires after seven days; signing out revokes that session.

### Password reset email

Password recovery sends a six-digit code that expires after 15 minutes. Configure an SMTP account before requesting a reset. Store the SMTP settings with .NET User Secrets so they stay outside the repository. Run these commands from the repository root:

```powershell
dotnet user-secrets set "Email:SmtpHost" "smtp.your-provider.example" --project backend/src/LifeOS.Api/LifeOS.Api.csproj
dotnet user-secrets set "Email:SmtpPort" "587" --project backend/src/LifeOS.Api/LifeOS.Api.csproj
dotnet user-secrets set "Email:SmtpUser" "your-smtp-user" --project backend/src/LifeOS.Api/LifeOS.Api.csproj
dotnet user-secrets set "Email:FromAddress" "lifeos@your-domain.example" --project backend/src/LifeOS.Api/LifeOS.Api.csproj
dotnet user-secrets set "Email:FromName" "LifeOS" --project backend/src/LifeOS.Api/LifeOS.Api.csproj
dotnet user-secrets set "Email:EnableSsl" "true" --project backend/src/LifeOS.Api/LifeOS.Api.csproj
$smtpPassword = Read-Host "SMTP password or app password" -AsSecureString
$plainPassword = [System.Net.NetworkCredential]::new("", $smtpPassword).Password
dotnet user-secrets set "Email:SmtpPassword" "$plainPassword" --project backend/src/LifeOS.Api/LifeOS.Api.csproj
Remove-Variable smtpPassword, plainPassword
```

Use the SMTP host, credentials, and sender address issued by your email provider. The desktop app loads these development secrets automatically when it starts the API. Reset requests are limited to five per IP address every ten minutes; codes allow five attempts and are stored only as password hashes. SMTP is also used for task reminder emails; reminders are checked while the local API is running.

## API

- `POST /api/auth/register` — create an account (`name`, `email`, `password`)
- `POST /api/auth/login` — sign in (`email`, `password`)
- `POST /api/auth/logout` — revoke the current bearer session
- `POST /api/auth/change-password` — change the signed-in account password (`currentPassword`, `newPassword`); other active sessions are revoked
- `GET /api/users/me` / `PUT /api/users/me` — read or update your profile (`name`, `email`)
- `GET /api/export` — download a ZIP backup of your workspace records and uploaded document files
- `GET /api/tasks` — list your tasks
- `POST /api/tasks` — create a task (`title`, optional `description`, `status`, `priority`, `dueAt`, `recurrence` of `none`, `daily`, `weekly`, or `monthly`, and `reminderAt`)
- `PUT /api/tasks/{id}` — update one of your tasks
- `DELETE /api/tasks/{id}` — delete one of your tasks
- `GET /api/finance?month=YYYY-MM` — get your month's INR income, expenses, balance, and transactions
- `POST /api/finance` — add an income or expense (`type`, `amount`, `category`, `occurredAt`, optional `note`)
- `PUT /api/finance/{id}` — update one of your transactions
- `DELETE /api/finance/{id}` — delete one of your transactions
- `POST /api/finance/budgets` — set or update a monthly category budget (`month`, `category`, `amount`)
- `DELETE /api/finance/budgets/{id}` — remove a category budget
- `GET /api/fitness/workouts?month=YYYY-MM` — list your workouts and monthly totals
- `POST /api/fitness/workouts` — log a workout (`name`, `type`, `durationMinutes`, `performedAt`, optional `notes`)
- `PUT /api/fitness/workouts/{id}` / `DELETE /api/fitness/workouts/{id}` — update or delete one of your workouts
- `GET /api/fitness/habits?month=YYYY-MM` — list active habits and the month's check-ins
- `POST /api/fitness/habits` — add a daily habit (`name`, optional `category`)
- `PUT /api/fitness/habits/{id}/check-ins/{yyyy-MM-dd}` — check off a day; use `DELETE` to undo it
- `DELETE /api/fitness/habits/{id}` — delete a habit and its check-ins
- `GET /api/documents?q=search&category=identity` — search your private document library
- `POST /api/documents` — upload a file and metadata as multipart form data
- `GET /api/documents/{id}/download` — download one of your files
- `PUT /api/documents/{id}` — update its title, category, or expiry date
- `DELETE /api/documents/{id}` — remove the file and its metadata
- `GET /api/career` — view your application and skill overview
- `POST /api/career/applications` — track an opportunity (`company`, `role`, `status`, optional dates/link/notes)
- `PUT /api/career/applications/{id}` / `DELETE /api/career/applications/{id}` — update or remove an application
- `POST /api/career/skills` — add a skill goal (`name`, `category`, `currentLevel`, `targetLevel`, optional goal date/notes)
- `PUT /api/career/skills/{id}` / `DELETE /api/career/skills/{id}` — update or remove a skill goal
- `GET /api/health` — basic health response

- `GET /api/assistant/status` — check whether the server has an OpenAI API key
- `POST /api/assistant/chat` — ask the assistant (`message`, recent `history`, selected `context` summaries)

Task requests require `Authorization: Bearer <token>`. Status values are `open`, `in-progress`, and `done`; priority values are `low`, `normal`, and `high`. Completing a recurring task creates its next occurrence. A reminder email is sent once at or after `reminderAt` when SMTP is configured and the local API is running. A user's tasks are only accessible to that user.

The Calendar view shows tasks that have due dates. All Tasks supports search, status and priority filters, and sorting. Finance includes monthly category budgets, spending progress, an expense-by-category chart, and a CSV export. Settings lets you update your profile, change your password, and download a ZIP backup containing workspace records and uploaded files.

Finance requests require the same bearer token. Amounts are entered in Indian rupees and may have up to two decimal places. Transaction type is `income` or `expense`; summaries are grouped by calendar month.

Fitness requests also require a bearer token. Workout types are `strength`, `cardio`, `mobility`, `sport`, or `other`. Habits can be checked off once per day; monthly views show each habit's check-in dates and workout time totals.

Document requests require a bearer token. Uploads accept PDF, DOCX, XLSX, TXT, PNG, and JPG files up to 15 MB. Files are stored under `App_Data/documents` outside the public web folder; metadata and file access are scoped to the signed-in account. The storage location can be changed with the `Documents:StoragePath` configuration setting.

Career records require a bearer token. Application stages are `saved`, `applied`, `interview`, `offer`, or `rejected`. Skill levels use a 1–5 scale and are stored per account.

The AI assistant uses a local Ollama model by default, so it does not need an API key or incur API usage charges. Install Ollama for Windows, then run `ollama run qwen3:4b` from PowerShell. The first run downloads about 2.5 GB; keep Ollama running while using LifeOS. The assistant sends the current message and recent chat to the selected provider. Workspace summaries are only included when selected for a message; document file contents are never included. Chat history is held in the browser tab and isn't saved by LifeOS.

### Local configuration and troubleshooting

To use OpenAI instead of the free local model, select the provider and set an API key from the repository root:

```powershell
dotnet user-secrets set "AI:Provider" "OpenAI" --project backend/src/LifeOS.Api/LifeOS.Api.csproj
dotnet user-secrets set "OPENAI_API_KEY" "your-api-key" --project backend/src/LifeOS.Api/LifeOS.Api.csproj
```

Restart LifeOS after changing the provider or key. Keep API keys out of source files and browser code. To return to the local model, set `AI:Provider` to `Ollama` or remove that User Secret; the default model is `qwen3:4b` at `http://localhost:11434`.

To run the automated assistant service checks:

```powershell
dotnet test backend/tests/LifeOS.Api.Tests/LifeOS.Api.Tests.csproj
```

### Windows desktop distribution

The desktop host references the backend DLL and bundles the frontend. Publishing still requires an installer that includes the .NET runtime and WebView2 Runtime; user data is kept separately under `%LOCALAPPDATA%\LifeOS`.
