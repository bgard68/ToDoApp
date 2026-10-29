/**
 * Architecture rules for the frontend, enforced in CI right after the tests
 * (see deploy.yml). Deterministic and fast: `npm run depcruise`.
 */
module.exports = {
  forbidden: [
    {
      name: 'no-circular',
      severity: 'error',
      comment:
        'Circular imports make modules impossible to test in isolation and ' +
        'can break bundling in ways that only show up at runtime.',
      from: {},
      to: { circular: true },
    },
    {
      name: 'prod-must-not-import-tests',
      severity: 'error',
      comment:
        'Production code must never reach into spec files or test helpers - ' +
        'that ships test scaffolding to users.',
      from: { path: '^src', pathNot: '\\.test\\.jsx?$|^src/test' },
      to: { path: '\\.test\\.jsx?$|^src/test' },
    },
    {
      name: 'no-unresolvable',
      severity: 'error',
      comment:
        'An import that does not resolve is a broken build waiting to happen.',
      from: {},
      to: { couldNotResolve: true },
    },
  ],
  options: {
    doNotFollow: { path: 'node_modules' },
  },
};
