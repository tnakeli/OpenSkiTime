// A small JSON Schema validator for the keywords the Live API schema uses. It is stricter than the schema in one way: an
// undocumented property is an error, so that everything the server sends is documented. An unknown keyword throws.
const annotations = new Set(['$schema', '$id', '$defs', 'title', 'description', 'format', 'deprecated']);
export function validate(root, name, value) {
  const errors = [];
  check(root, root.$defs[name] ?? fail(`Unknown schema ${name}`), value, name, errors);
  return errors;
}
const fail = message => { throw new Error(message); };
const typeOf = value => value === null ? 'null' : Array.isArray(value) ? 'array' : Number.isInteger(value) ? 'integer' : typeof value;
function check(root, schema, value, at, errors) {
  for (const [keyword, rule] of Object.entries(schema)) {
    if (annotations.has(keyword) || keyword === 'properties') continue;
    if (keyword === '$ref') {
      const target = root.$defs[rule.replace('#/$defs/', '')] ?? fail(`Unknown reference ${rule}`);
      check(root, target, value, at, errors);
    } else if (keyword === 'type') {
      const types = [rule].flat();
      if (!types.includes(typeOf(value))) errors.push(`${at}: ${typeOf(value)} is not ${types.join(' or ')}`);
    } else if (keyword === 'required') {
      if (typeOf(value) === 'object') for (const key of rule) if (!(key in value)) errors.push(`${at}: ${key} is missing`);
    } else if (keyword === 'enum') {
      if (!rule.includes(value)) errors.push(`${at}: ${JSON.stringify(value)} is not allowed`);
    } else if (keyword === 'minimum') {
      if (typeof value === 'number' && value < rule) errors.push(`${at}: ${value} is below ${rule}`);
    } else if (keyword === 'pattern') {
      if (typeof value === 'string' && !new RegExp(rule, 'u').test(value)) errors.push(`${at}: ${value} does not match ${rule}`);
    } else if (keyword === 'items') {
      if (Array.isArray(value)) value.forEach((item, i) => check(root, rule, item, `${at}[${i}]`, errors));
    } else if (keyword === 'oneOf') {
      const attempts = rule.map(option => { const found = []; check(root, option, value, at, found); return found; });
      // A null option explains nothing about an object; report the errors of the option of the same kind.
      if (attempts.filter(found => found.length === 0).length !== 1) errors.push(...(value === null ? attempts.flat() : attempts[0]));
    } else fail(`Unsupported schema keyword ${keyword} at ${at}`);
  }
  if (schema.properties && typeOf(value) === 'object') {
    for (const [key, item] of Object.entries(value)) {
      if (!schema.properties[key]) errors.push(`${at}: ${key} is not documented`);
      else check(root, schema.properties[key], item, `${at}.${key}`, errors);
    }
  }
}
