import { randomBytes, createHash } from "node:crypto";
import { mkdir, writeFile, readFile } from "node:fs/promises";
import { join } from "node:path";
import { homedir } from "node:os";

const id = process.argv[2];
if (!id || !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(id)
  || ["constructor", "prototype"].includes(id)) {
  throw new Error("Usage: node scripts/create-invite.mjs <user-id> (letters, digits, - and _)");
}
const token = randomBytes(32).toString("base64url");
const hash = createHash("sha256").update(token).digest("hex");
const directory = join(process.env.LOCALAPPDATA ?? homedir(), "BridgEN", "admin", "invites");
await mkdir(directory, { recursive: true });
const stamp = Date.now();
const tokenPath = join(directory, `${id}-${stamp}.token.txt`);
const registrationPath = join(directory, `${id}-${stamp}.registration.json`);
const registryPath = join(directory, "invitation-registry.json");
let registry = {};
try { registry = JSON.parse(await readFile(registryPath, "utf8")); }
catch (error) { if (error.code !== "ENOENT") throw error; }
// Issuing a new token for the same id rotates that user's previous token.
for (const [existingHash, userId] of Object.entries(registry)) {
  if (userId === id) delete registry[existingHash];
}
registry[hash] = id;
if (Object.keys(registry).length > 32) throw new Error("This beta supports at most 32 invitation users.");
const registration = JSON.stringify(registry, null, 2);
if (Buffer.byteLength(registration, "utf8") > 5000) throw new Error("Invitation registry exceeds the Secret size budget.");
await writeFile(tokenPath, token, { flag: "wx", mode: 0o600 });
await writeFile(registrationPath, JSON.stringify({ [hash]: id }, null, 2), { flag: "wx", mode: 0o600 });
await writeFile(registryPath, registration, { mode: 0o600 });
console.log(`Invite token file (give only to this user): ${tokenPath}`);
console.log(`Single user registration JSON: ${registrationPath}`);
console.log(`Set INVITE_TOKENS_JSON to the contents of the combined registry: ${registryPath}`);
