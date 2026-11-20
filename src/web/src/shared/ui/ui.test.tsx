import { render, screen } from '@testing-library/react';
import { createRef } from 'react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { Button } from './Button';
import { PageHeader } from './PageHeader';
import { SegmentedControl } from './SegmentedControl';

describe('Button', () => {
  it('è di tipo button per impostazione predefinita e ha un bersaglio di tocco di almeno 44 px', () => {
    render(<Button>Vai</Button>);
    const button = screen.getByRole('button', { name: 'Vai' });
    expect(button).toHaveAttribute('type', 'button');
    expect(button).toHaveClass('min-h-11', 'min-w-11');
  });

  it('inoltra il ref al pulsante (per gestire il focus)', () => {
    const ref = createRef<HTMLButtonElement>();
    render(<Button ref={ref}>Vai</Button>);
    expect(ref.current).toBe(screen.getByRole('button', { name: 'Vai' }));
  });

  it('chiama onClick e rispetta disabled', async () => {
    const user = userEvent.setup();
    const onClick = vi.fn();
    const { rerender } = render(<Button onClick={onClick}>Vai</Button>);
    await user.click(screen.getByRole('button', { name: 'Vai' }));
    expect(onClick).toHaveBeenCalledTimes(1);

    rerender(
      <Button onClick={onClick} disabled>
        Vai
      </Button>,
    );
    await user.click(screen.getByRole('button', { name: 'Vai' }));
    expect(onClick).toHaveBeenCalledTimes(1);
  });
});

describe('PageHeader', () => {
  it('mostra il titolo come h1 e la descrizione se presente', () => {
    const { rerender } = render(<PageHeader title="Titolo" description="Descrizione" />);
    expect(screen.getByRole('heading', { level: 1, name: 'Titolo' })).toBeInTheDocument();
    expect(screen.getByText('Descrizione')).toBeInTheDocument();

    rerender(<PageHeader title="Titolo" />);
    expect(screen.queryByText('Descrizione')).not.toBeInTheDocument();
  });
});

describe('SegmentedControl', () => {
  const options = [
    { value: 'a', label: 'Uno', content: '1' },
    { value: 'b', label: 'Due', content: '2' },
  ] as const;

  it('è un gruppo con pulsanti aria-pressed e nomi accessibili', () => {
    render(<SegmentedControl label="Scelta" value="b" options={options} onChange={() => undefined} />);
    const group = screen.getByRole('group', { name: 'Scelta' });
    expect(group).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Uno' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByRole('button', { name: 'Due' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('chiama onChange con il valore scelto', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<SegmentedControl label="Scelta" value="b" options={options} onChange={onChange} />);
    await user.click(screen.getByRole('button', { name: 'Uno' }));
    expect(onChange).toHaveBeenCalledWith('a');
  });
});
