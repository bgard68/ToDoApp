import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import DateField from './DateField.jsx';

describe('<DateField />', () => {
  it('auto-inserts slashes and emits an ISO date when complete', () => {
    const onChange = vi.fn();
    render(<DateField value="" onChange={onChange} ariaLabel="Due date" />);
    const input = screen.getByLabelText('Due date');
    fireEvent.change(input, { target: { value: '07192026' } });
    expect(input.value).toBe('07/19/2026');
    expect(onChange).toHaveBeenLastCalledWith('2026-07-19');
  });

  it('rejects an impossible date (emits empty)', () => {
    const onChange = vi.fn();
    render(<DateField value="" onChange={onChange} ariaLabel="Due date" />);
    const input = screen.getByLabelText('Due date');
    fireEvent.change(input, { target: { value: '02302026' } });
    expect(input.value).toBe('02/30/2026');
    expect(onChange).toHaveBeenLastCalledWith('');
  });

  it('clears the whole field', () => {
    const onChange = vi.fn();
    render(<DateField value="2026-07-19" onChange={onChange} ariaLabel="Due date" />);
    const input = screen.getByLabelText('Due date');
    expect(input.value).toBe('07/19/2026');
    fireEvent.change(input, { target: { value: '' } });
    expect(input.value).toBe('');
    expect(onChange).toHaveBeenLastCalledWith('');
  });
});
