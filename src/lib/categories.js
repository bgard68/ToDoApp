// Look up a category object by id from a fetched list; null id or unknown id -> null.
export function findCategory(categories, id) {
  if (id === null || id === undefined) return null;
  return categories.find((c) => c.id === id) || null;
}
