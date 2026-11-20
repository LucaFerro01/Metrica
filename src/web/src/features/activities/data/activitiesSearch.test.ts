import { describe, expect, it } from 'vitest';
import { MAX_PAGE, parseActivitiesSearch } from './activitiesSearch';

describe('parseActivitiesSearch', () => {
  it.each(['run', 'ride', 'swim', 'other'])('accetta lo sport %s', (sport) => {
    expect(parseActivitiesSearch({ sport }).sport).toBe(sport);
  });

  it.each([
    ['Run'],
    ['RUN'],
    [''],
    ['triathlon'],
    [' run'],
    [1],
    [null],
    [['run']],
    [{ sport: 'run' }],
    [undefined],
  ])('scarta lo sport %j (predefinito: tutti)', (sport) => {
    expect(parseActivitiesSearch({ sport }).sport).toBeUndefined();
  });

  it.each([
    [2, 2],
    [3, 3],
    ['7', 7],
    [MAX_PAGE, MAX_PAGE],
  ])('accetta la pagina %j', (raw, expected) => {
    expect(parseActivitiesSearch({ page: raw }).page).toBe(expected);
  });

  it.each([
    [1], // il predefinito non si riporta
    [0],
    [-1],
    [2.5],
    ['abc'],
    ['2.5'],
    ['-3'],
    [''],
    [MAX_PAGE + 1],
    [Number.NaN],
    [Number.POSITIVE_INFINITY],
    [99999999999],
    [null],
    [true],
    [[2]],
    [undefined],
  ])('scarta la pagina %j (predefinito: 1)', (page) => {
    expect(parseActivitiesSearch({ page }).page).toBeUndefined();
  });

  it('restituisce sempre le due chiavi (undefined sovrascrive il valore grezzo ereditato dal router)', () => {
    const result = parseActivitiesSearch({ sport: 'bogus', page: 0, altro: 'x' });
    expect(Object.keys(result).sort()).toEqual(['page', 'sport']);
    expect(result).toEqual({});
  });
});
