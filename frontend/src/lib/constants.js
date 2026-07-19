// Domain constants shared across the UI. Values must match the backend enums.

export const PRIORITIES = [
  { value: 0, label: 'Low' },
  { value: 1, label: 'Medium' },
  { value: 2, label: 'High' },
];

// Kanban lanes (order defines the columns left-to-right).
export const STATUSES = [
  { value: 0, label: 'To Do' },
  { value: 1, label: 'In Progress' },
  { value: 2, label: 'Done' },
];

// Named status values so board logic never relies on magic numbers.
export const STATUS = { ToDo: 0, InProgress: 1, Done: 2 };

// Neutral fallback used when a task has no category (or its category was deleted).
export const DEFAULT_CATEGORY_COLOR = '#64748b';
