const jsonHeaders = {
  "Content-Type": "application/json; charset=utf-8",
  "Cache-Control": "no-store"
};

function todayKey() {
  return new Date().toISOString().slice(0, 10);
}

async function incrementCounter(kv, key) {
  const current = Number.parseInt(await kv.get(key) || "0", 10);
  const next = Number.isFinite(current) ? current + 1 : 1;
  await kv.put(key, String(next));
  return next;
}

function errorResponse(message, status = 500) {
  return new Response(JSON.stringify({ ok: false, error: message }), {
    status,
    headers: jsonHeaders
  });
}

export async function onRequestPost({ env, request }) {
  if (!env.EXPORT_COUNTS) {
    return errorResponse("Missing EXPORT_COUNTS KV binding.");
  }

  let mode = "unknown";
  try {
    const data = await request.json();
    if (data && typeof data.mode === "string") {
      mode = data.mode.slice(0, 32);
    }
  } catch {
    // The count still matters even if the optional payload is missing.
  }

  const date = todayKey();

  await Promise.all([
    incrementCounter(env.EXPORT_COUNTS, "exports:dxf:total"),
    incrementCounter(env.EXPORT_COUNTS, `exports:dxf:date:${date}`),
    incrementCounter(env.EXPORT_COUNTS, `exports:dxf:mode:${mode}`)
  ]);

  return new Response(JSON.stringify({ ok: true }), {
    status: 202,
    headers: jsonHeaders
  });
}

export async function onRequestGet({ env }) {
  if (!env.EXPORT_COUNTS) {
    return errorResponse("Missing EXPORT_COUNTS KV binding.");
  }

  const total = Number.parseInt(await env.EXPORT_COUNTS.get("exports:dxf:total") || "0", 10);
  const today = Number.parseInt(await env.EXPORT_COUNTS.get(`exports:dxf:date:${todayKey()}`) || "0", 10);

  return new Response(JSON.stringify({
    total: Number.isFinite(total) ? total : 0,
    today: Number.isFinite(today) ? today : 0
  }), {
    headers: jsonHeaders
  });
}
