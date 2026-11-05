// Loads a generated collection with Postman's own collection SDK (the model Postman and Newman use when importing)
// and checks what a user would see for every request: method, URL, query, path variables, headers, body and auth.
//
//   node verify-import.mjs <collection.json> [--summary <out.json>]
//
// Exit code 0: every check passed. Exit code 1: at least one problem (listed on stderr).
import { readFileSync, writeFileSync } from 'node:fs';
import sdk from 'postman-collection';

const args = process.argv.slice(2);
const file = args[0];
const summaryIndex = args.indexOf('--summary');
const summaryPath = summaryIndex >= 0 ? args[summaryIndex + 1] : undefined;

if (!file) {
  console.error('Usage: node verify-import.mjs <collection.json> [--summary <out.json>]');
  process.exit(1);
}

const problems = [];
const fail = (where, message) => problems.push(`${where}: ${message}`);

const json = JSON.parse(readFileSync(file, 'utf8'));
const collection = new sdk.Collection(json);

if (!sdk.Collection.isCollection(collection)) fail('collection', 'the SDK did not recognise the document as a collection');
if (json.info?.schema !== 'https://schema.getpostman.com/json/collection/v2.1.0/collection.json') fail('info.schema', 'not the v2.1.0 schema');

const variables = new Map(collection.variables.all().map((v) => [v.key, v.value]));
const rows = [];

collection.forEachItem((item) => {
  const where = `${item.parent()?.name ?? '(root)'} / ${item.name}`;
  const request = item.request;
  if (!request) return;

  const method = request.method;
  const url = request.url;
  const raw = url.toString();
  const resolved = sdk.Property.replaceSubstitutions(raw, collection.variables);

  if (!['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].includes(method)) fail(where, `unsupported method ${method}`);
  if (!raw.startsWith('{{baseUrl}}')) fail(where, `URL does not start with {{baseUrl}}: ${raw}`);
  if (/\{\{(?!baseUrl\}\})[^}]+\}\}/.test(raw)) fail(where, `URL uses an unexpected variable: ${raw}`);

  const pathVariables = url.variables.all().map((v) => v.key);
  for (const segment of url.path ?? []) {
    if (typeof segment === 'string' && segment.startsWith(':') && !pathVariables.includes(segment.slice(1))) {
      fail(where, `path variable ${segment} is not declared`);
    }
  }

  let body = null;
  if (request.body) {
    const mode = request.body.mode;
    if (mode !== 'raw') fail(where, `body mode is ${mode}, expected raw`);
    try {
      body = JSON.parse(request.body.raw);
    } catch (error) {
      fail(where, `body is not valid JSON: ${error.message}`);
    }
    if (request.body.options?.raw?.language !== 'json') fail(where, 'body language is not json');
    if (request.headers.get('Content-Type') !== 'application/json') fail(where, 'missing Content-Type: application/json');
  }

  let auth = null;
  if (request.auth) {
    const params = Object.fromEntries(request.auth.parameters().all().map((p) => [p.key, p.value]));
    auth = { type: request.auth.type, ...params };
    if (request.auth.type !== 'bearer') fail(where, `auth type is ${request.auth.type}`);
    if (params.token !== '{{token}}') fail(where, `bearer token is not the {{token}} placeholder: ${params.token}`);
  }

  rows.push({
    folder: item.parent()?.name,
    name: item.name,
    method,
    url: raw,
    resolvedUrl: resolved,
    pathVariables,
    query: url.query.all().map((q) => ({ key: q.key, disabled: !!q.disabled })),
    headers: request.headers.all().map((h) => ({ key: h.key, disabled: !!h.disabled })),
    body,
    auth,
  });
});

if (variables.get('token') !== '') fail('variable.token', 'the token variable must be empty');
if (!variables.has('baseUrl')) fail('variable.baseUrl', 'missing');

const summary = {
  file,
  sdk: `postman-collection ${sdk.version ?? ''}`.trim(),
  collection: collection.name,
  folders: collection.items.all().length,
  requests: rows.length,
  variables: Object.fromEntries(variables),
  problems,
  items: rows,
};

if (summaryPath) writeFileSync(summaryPath, JSON.stringify(summary, null, 2) + '\n');

console.log(`${collection.name}: ${summary.folders} folder(s), ${rows.length} request(s)`);
for (const row of rows) {
  const flags = [
    row.body ? 'body' : null,
    row.auth ? 'bearer' : null,
    row.query.length ? `query(${row.query.map((q) => q.key + (q.disabled ? '?' : '')).join(',')})` : null,
    row.pathVariables.length ? `path(${row.pathVariables.join(',')})` : null,
  ].filter(Boolean).join(' ');
  console.log(`  ${row.folder.padEnd(10)} ${row.method.padEnd(7)} ${row.url.padEnd(44)} ${row.name}${flags ? '  [' + flags + ']' : ''}`);
}

if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s):`);
  for (const problem of problems) console.error(`  - ${problem}`);
  process.exit(1);
}

console.log('\nImport check passed: no problems.');
