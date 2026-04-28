/**
 * GET /api/personas — return all personas (single source of truth for the UI).
 */
import { loadPersonas } from "../utils/personasStore";

export default defineEventHandler(async () => {
  return await loadPersonas();
});
