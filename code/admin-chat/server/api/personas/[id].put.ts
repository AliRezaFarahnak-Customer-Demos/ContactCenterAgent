/**
 * PUT /api/personas/:id — patch a persona.
 * Called by the CallComposer with a 1-second debounce on any field change.
 */
import { updatePersona } from "../../utils/personasStore";

export default defineEventHandler(async (event) => {
  const id = getRouterParam(event, "id");
  if (!id) {
    setResponseStatus(event, 400);
    return { success: false, error: "id is required" };
  }
  const body = await readBody<Record<string, unknown>>(event);
  if (!body || typeof body !== "object") {
    setResponseStatus(event, 400);
    return { success: false, error: "body is required" };
  }
  const updated = await updatePersona(id, body as never);
  if (!updated) {
    setResponseStatus(event, 404);
    return { success: false, error: `persona '${id}' not found` };
  }
  return { success: true, persona: updated };
});
