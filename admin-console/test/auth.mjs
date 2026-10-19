// 登录续期回归，无外部依赖：node --test admin-console/test/auth.mjs
import test from "node:test";
import assert from "node:assert/strict";
import wcb from "../js/api.js";

const { AdminClient, ApiError } = wcb;
const tokens = (suffix, expiresAt = new Date(Date.now() + 3600000).toISOString()) => ({
  accessToken: "access-" + suffix, refreshToken: "refresh-" + suffix, expiresAt,
});
function deferred() {
  let resolve;
  const promise = new Promise((done) => { resolve = done; });
  return { promise, resolve };
}

test("并发管理请求只轮换一次 refresh token，且都使用新 access token", async () => {
  const gate = deferred();
  let refreshes = 0;
  const headers = [];
  const client = new AdminClient({ json: async (method, path, body, auth) => {
    if (path === "/v1/auth/refresh") {
      refreshes++;
      assert.equal(body.refreshToken, "refresh-old");
      await gate.promise;
      return tokens("new");
    }
    headers.push(auth.Authorization);
    return { items: [] };
  } });
  client.setTokens(tokens("old", new Date(0).toISOString()));
  const pending = client.listRequests("pending");
  const ledger = client.listRequests("confirmed");
  gate.resolve();
  await Promise.all([pending, ledger]);
  assert.equal(refreshes, 1);
  assert.deepEqual(headers, ["Bearer access-new", "Bearer access-new"]);
});

test("旧请求迟到的 401 不会再次轮换已经更新的 refresh token", async () => {
  const gate = deferred();
  let refreshes = 0;
  const client = new AdminClient({ json: async (method, path, body, auth) => {
    if (path === "/v1/auth/refresh") { refreshes++; return tokens("new"); }
    if (auth.Authorization === "Bearer access-old") {
      if (path.includes("confirmed")) await gate.promise;
      throw new ApiError(401, "unauthorized");
    }
    assert.equal(auth.Authorization, "Bearer access-new");
    return { items: [] };
  } });
  client.setTokens(tokens("old"));
  const first = client.listRequests("pending");
  const delayed = client.listRequests("confirmed");
  await first;
  gate.resolve();
  await delayed;
  assert.equal(refreshes, 1);
});

test("403 无管理员权限不会刷新登录凭证", async () => {
  let calls = 0;
  const client = new AdminClient({ json: async () => { calls++; throw new ApiError(403, "forbidden"); } });
  client.setTokens(tokens("old"));
  await assert.rejects(client.listRequests("pending"), (err) => err.status === 403);
  assert.equal(calls, 1);
});

for (const outcome of ["expired", "reused"]) {
  test("续期终态 " + outcome + " 明确要求重新登录", async () => {
    const client = new AdminClient({ json: async () => {
      if (outcome === "reused") throw new ApiError(409, "conflict", "reused", "request-1");
      return { status: "expired" };
    } });
    client.setTokens(tokens("old", new Date(0).toISOString()));
    await assert.rejects(client.listRequests("pending"), (err) =>
      err.status === 401 && (outcome !== "reused" || err.requestId === "request-1"));
  });
}

test("503 保留原凭证，下一次请求可以重新续期", async () => {
  let refreshes = 0;
  const client = new AdminClient({ json: async (method, path, body) => {
    if (path === "/v1/auth/refresh") {
      assert.equal(body.refreshToken, "refresh-old");
      if (++refreshes === 1) throw new ApiError(503, "unavailable");
      return tokens("new");
    }
    return { items: [] };
  } });
  client.setTokens(tokens("old", new Date(0).toISOString()));
  await assert.rejects(client.listRequests("pending"), (err) => err.status === 503);
  await client.listRequests("pending");
  assert.equal(refreshes, 2);
});

test("退出后迟到的续期结果不能恢复凭证", async () => {
  const gate = deferred();
  const changed = [];
  const client = new AdminClient({ json: async (method, path, body, auth) => {
    if (path === "/v1/auth/refresh") { await gate.promise; return tokens("new"); }
    assert.equal(auth.Authorization, undefined);
    throw new ApiError(401, "unauthorized");
  } });
  client.onTokensChanged((value) => changed.push(value));
  client.setTokens(tokens("old", new Date(0).toISOString()));
  const pending = client.listRequests("pending");
  client.setTokens(null);
  gate.resolve();
  await assert.rejects(pending, (err) => err.status === 401);
  assert.deepEqual(changed, []);
});
