Debug and run the Contact Center Agent solution locally, then open the app in Playwright.

## Steps

1. **Install dependencies**

   ```bash
   cd code/admin-chat
   npm install
   ```

   This runs `nuxt prepare` and `dotnet restore` via the `postinstall` script.

2. **Free ports 3000 and 8000** (if occupied from a previous run)

   ```powershell
   Get-NetTCPConnection -LocalPort 3000,8000 -ErrorAction SilentlyContinue |
     ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
   ```

3. **Start dev servers** (from `code/admin-chat`)

   ```bash
   npm run dev
   ```

   This launches concurrently:
   - **Nuxt 3 UI** on `http://localhost:3000`
   - **.NET Agent** on `http://localhost:8000`

4. **Wait for both servers** to show as listening before proceeding.
   Verify with:

   ```powershell
   Get-NetTCPConnection -LocalPort 3000,8000 -ErrorAction SilentlyContinue |
     Select-Object LocalPort, State
   ```

   Both ports should show `Listen`.

## Notes

- The `npm run dev` command must run from `code/admin-chat` (where `package.json` lives), not the repo root.
- Background terminals always start from the workspace root, so always `cd` into `code/admin-chat` in the same command line.
- The .NET agent config (Azure OpenAI endpoint) is set via `dotnet user-secrets` in `code/admin-chat/agent/`. See the repo copilot-instructions for details.
