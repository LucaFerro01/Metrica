import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { pendingUploads, usePendingUploads } from './pendingUploads';

const first = { externalId: 'a'.repeat(64), uploadId: '11111111-1111-1111-1111-111111111111', acceptedAt: 1000 };
const second = { externalId: 'b'.repeat(64), uploadId: '22222222-2222-2222-2222-222222222222', acceptedAt: 2000 };

beforeEach(() => pendingUploads.clear());
afterEach(() => pendingUploads.clear());

describe('store degli upload in sospeso', () => {
  it('parte vuoto', () => {
    expect(pendingUploads.getSnapshot()).toEqual([]);
  });

  it('add registra externalId, uploadId e istante di avvio, in ordine di inserimento', () => {
    pendingUploads.add(first);
    pendingUploads.add(second);
    expect(pendingUploads.getSnapshot()).toEqual([first, second]);
  });

  it('non registra nient\'altro oltre ai tre campi (niente nome file né contenuto)', () => {
    pendingUploads.add({ ...first, name: 'segreto.gpx', content: 'dati' } as typeof first);
    expect(Object.keys(pendingUploads.getSnapshot()[0] ?? {}).sort()).toEqual([
      'acceptedAt',
      'externalId',
      'uploadId',
    ]);
  });

  it('add dello stesso externalId non crea un doppione e non cambia l\'istantanea', () => {
    pendingUploads.add(first);
    const before = pendingUploads.getSnapshot();
    pendingUploads.add({ ...first, acceptedAt: 9999 });
    expect(pendingUploads.getSnapshot()).toBe(before);
    expect(pendingUploads.getSnapshot()).toHaveLength(1);
    expect(pendingUploads.getSnapshot()[0]?.acceptedAt).toBe(1000);
  });

  it('remove toglie per externalId e ignora uno sconosciuto', () => {
    pendingUploads.add(first);
    pendingUploads.add(second);
    pendingUploads.remove(first.externalId);
    expect(pendingUploads.getSnapshot()).toEqual([second]);
    const before = pendingUploads.getSnapshot();
    pendingUploads.remove('sconosciuto');
    expect(pendingUploads.getSnapshot()).toBe(before);
  });

  it('clear svuota; su un store già vuoto non notifica', () => {
    pendingUploads.add(first);
    pendingUploads.clear();
    expect(pendingUploads.getSnapshot()).toEqual([]);
    const listener = vi.fn();
    const unsubscribe = pendingUploads.subscribe(listener);
    pendingUploads.clear();
    expect(listener).not.toHaveBeenCalled();
    unsubscribe();
  });

  it('l\'istantanea non viene mutata: ogni modifica ne produce una nuova', () => {
    pendingUploads.add(first);
    const before = pendingUploads.getSnapshot();
    pendingUploads.add(second);
    expect(before).toEqual([first]);
    expect(pendingUploads.getSnapshot()).not.toBe(before);
  });

  it('subscribe notifica a ogni modifica e unsubscribe smette', () => {
    const listener = vi.fn();
    const unsubscribe = pendingUploads.subscribe(listener);
    pendingUploads.add(first);
    pendingUploads.remove(first.externalId);
    expect(listener).toHaveBeenCalledTimes(2);
    unsubscribe();
    pendingUploads.add(second);
    expect(listener).toHaveBeenCalledTimes(2);
  });

  it('usePendingUploads segue le modifiche', () => {
    const { result } = renderHook(() => usePendingUploads());
    expect(result.current).toEqual([]);
    act(() => pendingUploads.add(first));
    expect(result.current).toEqual([first]);
    act(() => pendingUploads.add(second));
    expect(result.current).toEqual([first, second]);
    act(() => pendingUploads.remove(first.externalId));
    expect(result.current).toEqual([second]);
    act(() => pendingUploads.clear());
    expect(result.current).toEqual([]);
  });

  it('più componenti vedono lo stesso stato', () => {
    const a = renderHook(() => usePendingUploads());
    const b = renderHook(() => usePendingUploads());
    act(() => pendingUploads.add(first));
    expect(a.result.current).toEqual([first]);
    expect(b.result.current).toEqual([first]);
  });
});
