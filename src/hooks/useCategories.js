import { useCallback, useEffect, useState } from 'react';
import { CategoryApi } from '../lib/apiClient.js';

/** Loads and exposes the signed-in user's categories, with a manual reload. */
export function useCategories() {
  const [categories, setCategories] = useState([]);

  const reload = useCallback(async () => {
    setCategories(await CategoryApi.list());
  }, []);

  useEffect(() => {
    reload();
  }, [reload]);

  return { categories, reload };
}
