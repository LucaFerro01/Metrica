import { describe, expect, it } from 'vitest';
import en from './locales/en.json';
import it_ from './locales/it.json';

type Tree = { [key: string]: string | Tree };

/** Elenco ordinato dei percorsi (a punti) delle foglie di un albero di traduzioni. */
function leafPaths(tree: Tree, prefix = ''): string[] {
  return Object.entries(tree)
    .flatMap(([key, value]) =>
      typeof value === 'string' ? [`${prefix}${key}`] : leafPaths(value, `${prefix}${key}.`),
    )
    .sort();
}

function leaves(tree: Tree, prefix = ''): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(tree)) {
    if (typeof value === 'string') result[`${prefix}${key}`] = value;
    else Object.assign(result, leaves(value, `${prefix}${key}.`));
  }
  return result;
}

function placeholders(text: string): string[] {
  return [...text.matchAll(/\{\{\s*([^}\s,]+)[^}]*\}\}/g)].map((m) => m[1] ?? '').sort();
}

describe('parità delle traduzioni it/en', () => {
  it('hanno esattamente le stesse chiavi (confronto profondo)', () => {
    expect(leafPaths(en)).toEqual(leafPaths(it_));
  });

  it('non hanno testi vuoti', () => {
    for (const [path, text] of [...Object.entries(leaves(it_)), ...Object.entries(leaves(en))]) {
      expect(text.trim(), path).not.toBe('');
    }
  });

  it('hanno gli stessi segnaposto {{...}} per ogni chiave', () => {
    const italian = leaves(it_);
    const english = leaves(en);
    for (const [path, text] of Object.entries(italian)) {
      expect(placeholders(english[path] ?? ''), path).toEqual(placeholders(text));
    }
  });

  it('il confronto dei segnaposto riconosce una differenza (controllo del controllo)', () => {
    expect(placeholders('Ciao {{nome}}, hai {{ count }} attività')).toEqual(['count', 'nome']);
    expect(placeholders('Nessun segnaposto')).toEqual([]);
  });
});
