import { describe, it, expect } from 'vitest';
import {
  mergeChatHistory,
  alreadyLogged,
  historyEntry,
  type ChatEntry,
  type ChatHistoryRow,
} from '../src/lib/stores/chatHistory';

// Task 110e: world/guild/News history merged into the live log at sign-in.

const row = (id: number, at: number, text: string, channel = 0, sender = 7): ChatHistoryRow => ({
  Id: id,
  ChannelType: channel,
  SenderPlayerId: sender,
  MessageText: text,
  SentAtEpochMs: at,
});

const live = (id: number, at: number, text: string, channel = 0, sender = 7): ChatEntry => ({
  id,
  channelType: channel,
  senderPlayerId: sender,
  text,
  atMs: at,
});

describe('mergeChatHistory', () => {
  it('fills an empty log, newest first', () => {
    const merged = mergeChatHistory([], [row(1, 100, 'a'), row(2, 200, 'b')], 200);
    expect(merged.map((e) => e.text)).toEqual(['b', 'a']);
  });

  it('gives history rows negative ids, so the dock never counts them unread', () => {
    const merged = mergeChatHistory([], [row(41, 100, 'a')], 200);
    expect(merged[0].id).toBe(-41);
    // ChatDock: unread = max(0, newestId - seenId) with seenId starting at 0.
    expect(Math.max(0, merged[0].id - 0)).toBe(0);
  });

  it('drops a history row the live socket already delivered', () => {
    const log = [live(3, 200, 'b')];
    const merged = mergeChatHistory(log, [row(1, 100, 'a'), row(2, 200, 'b')], 200);
    expect(merged.map((e) => e.text)).toEqual(['b', 'a']);
    expect(merged[0].id).toBe(3); // the live copy wins
  });

  it('is idempotent across a reconnect fetching the same rows again', () => {
    const rows = [row(1, 100, 'a'), row(2, 200, 'b')];
    const once = mergeChatHistory([], rows, 200);
    expect(mergeChatHistory(once, rows, 200)).toBe(once);
  });

  it('keeps the same instant from different senders or channels apart', () => {
    const merged = mergeChatHistory(
      [live(1, 100, 'hi', 0, 7)],
      [row(5, 100, 'hi', 0, 8), row(6, 100, 'guild', 1, 7)],
      200,
    );
    expect(merged).toHaveLength(3);
  });

  it('respects the log cap by dropping the oldest', () => {
    const rows = Array.from({ length: 10 }, (_, i) => row(i + 1, 1000 + i, `m${i}`));
    const merged = mergeChatHistory([], rows, 4);
    expect(merged.map((e) => e.text)).toEqual(['m9', 'm8', 'm7', 'm6']);
  });
});

describe('alreadyLogged', () => {
  it('recognises a live packet history already brought in', () => {
    const log = [historyEntry(row(9, 500, 'x', 3, 0))];
    expect(alreadyLogged(log, { channelType: 3, senderPlayerId: 0, atMs: 500 })).toBe(true);
    expect(alreadyLogged(log, { channelType: 3, senderPlayerId: 0, atMs: 501 })).toBe(false);
  });
});
