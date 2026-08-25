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

  it('shows an existing ISO value as a masked date', () => {
    render(<DateField value="2026-07-19" onChange={vi.fn()} ariaLabel="Due date" />);

    expect(screen.getByLabelText('Due date')).toHaveValue('07/19/2026');
  });

  it('shows nothing for an empty value', () => {
    render(<DateField value="" onChange={vi.fn()} ariaLabel="Due date" />);

    expect(screen.getByLabelText('Due date')).toHaveValue('');
  });

  it('shows nothing for a value that is not a full date', () => {
    render(<DateField value="2026-07" onChange={vi.fn()} ariaLabel="Due date" />);

    expect(screen.getByLabelText('Due date')).toHaveValue('');
  });

  it('adopts a value set from outside', () => {
    const { rerender } = render(<DateField value="" onChange={vi.fn()} ariaLabel="Due date" />);

    rerender(<DateField value="2026-01-02" onChange={vi.fn()} ariaLabel="Due date" />);

    expect(screen.getByLabelText('Due date')).toHaveValue('01/02/2026');
  });

  it('emits nothing until the date is complete', () => {
    const onChange = vi.fn();
    render(<DateField value="" onChange={onChange} ariaLabel="Due date" />);
    const input = screen.getByLabelText('Due date');

    fireEvent.change(input, { target: { value: '0719' } });

    expect(input).toHaveValue('07/19');
    expect(onChange).toHaveBeenLastCalledWith('');
  });

  it('ignores extra digits past the eighth', () => {
    const onChange = vi.fn();
    render(<DateField value="" onChange={onChange} ariaLabel="Due date" />);

    fireEvent.change(screen.getByLabelText('Due date'), { target: { value: '071920261234' } });

    expect(screen.getByLabelText('Due date')).toHaveValue('07/19/2026');
    expect(onChange).toHaveBeenLastCalledWith('2026-07-19');
  });

  it('rejects a month outside 1-12', () => {
    const onChange = vi.fn();
    render(<DateField value="" onChange={onChange} ariaLabel="Due date" />);

    fireEvent.change(screen.getByLabelText('Due date'), { target: { value: '13/01/2026' } });

    expect(onChange).toHaveBeenLastCalledWith('');
  });

  it('accepts a leap day in a leap year', () => {
    const onChange = vi.fn();
    render(<DateField value="" onChange={onChange} ariaLabel="Due date" />);

    fireEvent.change(screen.getByLabelText('Due date'), { target: { value: '02/29/2024' } });

    expect(onChange).toHaveBeenLastCalledWith('2024-02-29');
  });

  it('opens the native picker from the calendar button', async () => {
    const { container } = render(<DateField value="" onChange={vi.fn()} ariaLabel="Due date" />);
    const native = container.querySelector('.date-native');
    native.showPicker = vi.fn();

    fireEvent.click(screen.getByLabelText('Open calendar'));

    expect(native.showPicker).toHaveBeenCalled();
  });

  it('focuses the native input when the browser has no picker API', async () => {
    const { container } = render(<DateField value="" onChange={vi.fn()} ariaLabel="Due date" />);
    const native = container.querySelector('.date-native');
    native.showPicker = vi.fn(() => { throw new Error('not supported'); });
    const focus = vi.spyOn(native, 'focus');

    fireEvent.click(screen.getByLabelText('Open calendar'));

    expect(focus).toHaveBeenCalled();
  });

  it('fills the bar from a date picked in the native control', () => {
    const onChange = vi.fn();
    const { container } = render(<DateField value="" onChange={onChange} ariaLabel="Due date" />);

    fireEvent.change(container.querySelector('.date-native'), { target: { value: '2026-03-04' } });

    expect(screen.getByLabelText('Due date')).toHaveValue('03/04/2026');
    expect(onChange).toHaveBeenLastCalledWith('2026-03-04');
  });

  it('defaults its label', () => {
    render(<DateField onChange={vi.fn()} />);

    expect(screen.getByLabelText('Date')).toBeInTheDocument();
  });

  it('still formats when the browser refuses to move the caret', () => {
    const onChange = vi.fn();
    render(<DateField value="" onChange={onChange} ariaLabel="Due date" />);
    const input = screen.getByLabelText('Due date');
    vi.spyOn(input, 'setSelectionRange').mockImplementation(() => {
      throw new Error('not supported on this input type');
    });

    fireEvent.change(input, { target: { value: '07192026' } });

    expect(input).toHaveValue('07/19/2026');
    expect(onChange).toHaveBeenLastCalledWith('2026-07-19');
  });
});
