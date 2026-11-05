# LifeOS User Guide

LifeOS keeps tasks, calendar items, finances, fitness habits, documents, career goals, and an optional AI chat in one personal workspace.

## Install and open LifeOS

1. Run `LifeOS-Setup-x64.exe` and follow the installer prompts. Choose the desktop shortcut if you want one.
2. If Windows says WebView2 is missing, install the Microsoft Edge WebView2 Runtime, then open LifeOS again. It is already included on most Windows 11 computers.
3. Launch LifeOS from the Start menu or desktop shortcut.
4. Choose **Create account** the first time. Use your email and a password of at least 10 characters. Later, choose **Sign in**.

Your local workspace data is stored under `%LOCALAPPDATA%\LifeOS`. Uninstalling or upgrading the app keeps this data.

## Use your workspace

- **My day / All tasks:** Add tasks, set due dates, priorities, and statuses. All Tasks lets you search and filter. Completing a recurring task creates its next occurrence.
- **Calendar:** Review tasks that have due dates.
- **Finance:** Record income and expenses, set monthly category budgets, review spending, and export a CSV.
- **Fitness:** Log workouts and check off daily habits.
- **Documents:** Upload and search personal files. Supported types are PDF, DOCX, XLSX, TXT, PNG, and JPG; files can be up to 15 MB.
- **Career:** Track job applications and skill goals.
- **Settings:** Update your profile or password, back up your workspace, and restore a backup.

## Use PostgreSQL with the Windows desktop app

The desktop app uses SQLite by default. To use the PostgreSQL server installed on this PC, first make a workspace backup in **Settings**. Then close LifeOS and create `%LOCALAPPDATA%\LifeOS\appsettings.json` with this content, replacing `YOUR_POSTGRES_PASSWORD` with the password you set for PostgreSQL's `postgres` account:

```json
{
  "LifeOS": {
    "DatabaseProvider": "PostgreSQL"
  },
  "ConnectionStrings": {
    "LifeOS": "Host=localhost;Port=5432;Database=lifeos;Username=postgres;Password=YOUR_POSTGRES_PASSWORD"
  }
}
```

Start LifeOS again. It will create the PostgreSQL tables automatically. Create an account in this new database, sign in, then use **Settings > Restore** to import the backup ZIP. The backup restores workspace records and uploaded documents; it does not move the account password. In pgAdmin, expand **Databases > lifeos > Schemas > public > Tables** and refresh to see the tables. New tasks, finance entries, fitness records, document metadata, and career records will then be stored in PostgreSQL. Uploaded document files remain in the local LifeOS data folder and are included in workspace backups.

The PostgreSQL connection password is stored in this per-user config file. Keep it private and do not commit the file to GitHub. To return to the existing SQLite database, change `DatabaseProvider` to `SQLite` and restart LifeOS.

## AI assistant (optional)

The assistant uses Ollama locally by default. Install Ollama for Windows, then run this once in PowerShell:

```powershell
ollama run qwen3:4b
```

The first run downloads the model (about 2.5 GB). Leave Ollama running while chatting with LifeOS. The assistant sends your message and recent chat to the configured provider. Workspace summaries are sent only when you select them for that message. Document contents are not sent, and chat history stays in the browser tab rather than being saved in LifeOS.

## Back up or restore your workspace

In **Settings**, choose the workspace backup action to download a ZIP containing your workspace records and uploaded documents. Save it somewhere separate from your computer, such as an external drive.

On Android, use **Download backup** in Settings; the ZIP is saved in the device's **Downloads** folder as `lifeos-backup-YYYY-MM-DD.zip`. To restore it, choose that ZIP from **Downloads** in the Android file picker.

To restore, select a LifeOS backup ZIP in Settings and confirm. Restoring **replaces the signed-in account's current tasks, finance, fitness, documents, and career records** with the backup contents. It keeps the account profile and password. Make a fresh backup first if you may need the current data.

## Mobile app development (Android and iPhone)

The mobile shell is prepared with Capacitor, but mobile sign-in and cross-device sync require a deployed HTTPS LifeOS API. The included Render Blueprint is configured for a free demo. Render's free web service has temporary storage: the SQLite database and uploaded documents can be erased when the service restarts, redeploys, or spins down after inactivity. Do not use this free setup for important data. The included Docker Compose setup binds to `127.0.0.1` for local development only; do not expose that HTTP port directly to the internet.

### Deploy the API on Render

1. Push the project to a GitHub repository you control. Keep passwords, API keys, and local data files out of the repository.
2. Sign in to Render, choose **New > Blueprint**, connect the repository, and select its root `render.yaml` file.
3. Review the proposed resources before creating them. The Blueprint requests a free web service in Singapore and no persistent disk. Render's free web services spin down after 15 minutes without traffic and use temporary storage, so data may be lost. The API is public so your phone can reach it, and Render provides the HTTPS URL.
4. Wait for the service to deploy, then check `https://<service-name>.onrender.com/api/health`. A healthy response is `{"status":"ok"}`.
5. Keep the Render URL; use it as `LIFEOS_API_BASE_URL` when building the mobile app below.

This is for testing only; keep a workspace backup and expect to restore it if Render clears temporary files. Do not scale it to multiple instances. The Blueprint selects OpenAI for the hosted AI provider, but does not include an API key; adding `OPENAI_API_KEY` is optional and OpenAI usage may incur charges. SMTP is also optional; Render free services cannot send outbound SMTP on ports 25, 465, or 587, so password reset and reminder emails need a different mail setup.

### Connect the Windows app to the shared workspace

The Windows app stays local unless you set its cloud API address. Before switching, open LifeOS in its current local mode and download a workspace backup from **Settings**. After the Render API is live, set its URL in PowerShell (replace the example with your actual Render URL):

```powershell
[Environment]::SetEnvironmentVariable("LifeOS__ApiBaseUrl", "https://lifeos-api.onrender.com", "User")
```

Close and reopen LifeOS. It will use the hosted API. Create an account there, then restore the backup you downloaded. Build the Android app with the same API URL below and sign in to that same account. Windows and Android will then read and write the shared workspace. The local Windows database is left in place. To switch Windows back to local data, clear the setting and reopen the app:

```powershell
[Environment]::SetEnvironmentVariable("LifeOS__ApiBaseUrl", $null, "User")
```

To check the API locally in Docker, first install Docker Desktop, then run this from the repository root:

```powershell
docker compose up --build
```

That starts a local API for development at `http://localhost:8080`. Check it from PowerShell with `Invoke-RestMethod http://localhost:8080/api/health`. This local HTTP setup is for API development only; it is not a public deployment and cannot be used by the production mobile build.

For Android development, install Node.js 22 or newer and Android Studio with an Android SDK. After deploying the API behind HTTPS, build the mobile web assets and open the Android project:

```powershell
cd frontend
npm.cmd install
$env:LIFEOS_API_BASE_URL = "https://your-api-domain.example"
npm.cmd run android:sync
npm.cmd run android:open
```

In Android Studio, run the app on an emulator or connected phone. The `LIFEOS_API_BASE_URL` value is embedded in the generated mobile assets; keep it as the API's public HTTPS address. Never put database passwords, SMTP credentials, or AI keys in the mobile app.

iPhone builds need macOS with Xcode. On that Mac, install dependencies, set the same HTTPS API address, then run `npm run ios:add`, `npm run ios:sync`, and `npm run ios:open` from `frontend`.

## Troubleshooting

- **LifeOS cannot start / WebView2 error:** Install or repair the Microsoft Edge WebView2 Runtime and reopen the app.
- **AI does not reply:** Confirm Ollama is installed and running, then run `ollama run qwen3:4b` in PowerShell. If your setup uses OpenAI, check that the provider and API key are configured by whoever maintains the installation.
- **Password recovery email does not arrive:** Email recovery requires the app administrator to configure SMTP. Check spam or contact the person who provided LifeOS.
- **Reminder email does not arrive:** Task reminders also require SMTP configuration and the local LifeOS service to be running when the reminder is due.
- **Need help diagnosing a problem:** Note what you were doing and the exact error message. Avoid sharing passwords, API keys, or private documents when requesting help.
