Debug and run the Contact Center Agent solution locally, then open the app in Playwright.

## Steps

1. **Install dependencies**

   ```bash
   cd code/admin-chat
   npm install
   ```

   This runs `nuxt prepare` via the `postinstall` script.

2. **Free port 3000** (if occupied from a previous run)

   ```powershell
   Get-NetTCPConnection -LocalPort 3000 -ErrorAction SilentlyContinue |
     ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
   ```

3. **Start the dev server** (from `code/admin-chat`)

   ```bash
   npm run dev
   ```

   admin-chat is a single Nuxt 3 process — **Nuxt 3 SPA** on `http://localhost:3000`.
   (The co-located .NET sidecar on port 8000 was removed; see "Removed: AdminChat .NET sidecar"
   in the repo copilot-instructions.)

4. **Wait for the server** to show as listening before proceeding.
   Verify with:

   ```powershell
   Get-NetTCPConnection -LocalPort 3000 -ErrorAction SilentlyContinue |
     Select-Object LocalPort, State
   ```

   Port 3000 should show `Listen`.

## Notes

- The `npm run dev` command must run from `code/admin-chat` (where `package.json` lives), not the repo root.
- Background terminals always start from the workspace root, so always `cd` into `code/admin-chat` in the same command line.
- Server-side config comes from `runtimeConfig` in `nuxt.config.ts` (env vars, e.g. `NUXT_CALLER_AGENT_URL`, `NUXT_ACS_PHONE_NUMBER`). Set them in the same command line before `npm run dev` to exercise features that depend on them.
