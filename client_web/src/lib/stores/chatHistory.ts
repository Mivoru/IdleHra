// Modul: chat HISTORY, task 110e (2026-10-02).
//
// World, guild and announcement chat used to exist only as live WebSocket
// packets, so a player signing in a minute after a conversation saw "Nothing
// in this channel yet". The server writes those channels down now and
// GET /api/v1/chat/recent returns the newest 50 of each (filtered as the live
// path is: own guild only, nothing from a blocked player; whispers are not in
// it - they have their own endpoints).
//
// Two sources reach one log, so the merge has one rule: a message is the
// triple (channel, sender, server timestamp). The server stamps the row and
// the live packet with the SAME instant, so a message that arrives live while
// the history request is in flight - or a history row whose live packet comes
// a moment after the response - appears once, never twice.

export interface ChatEntry {
  /**
   * Live arrivals take a positive, increasing id at receipt. History rows take
   * the NEGATIVE of their server row id, so they can never look newer than a
   * live message to the dock's unread counter (`newest id - seen id`), which
   * would otherwise greet every sign-in with "50 unread".
   */
  id: number;
  senderPlayerId: number;
  channelType: number;
  text: string;
  atMs: number;
}

/** One row of GET /api/v1/chat/recent (ChatHistory.Entry on the server). */
export interface ChatHistoryRow {
  Id: number;
  ChannelType: number;
  SenderPlayerId: number;
  MessageText: string;
  SentAtEpochMs: number;
}

export function chatKey(e: Pick<ChatEntry, 'channelType' | 'senderPlayerId' | 'atMs'>): string {
  return `${e.channelType}:${e.senderPlayerId}:${e.atMs}`;
}

export function historyEntry(row: ChatHistoryRow): ChatEntry {
  return {
    id: -Math.abs(Number(row.Id)),
    senderPlayerId: Number(row.SenderPlayerId),
    channelType: Number(row.ChannelType),
    text: row.MessageText,
    atMs: Number(row.SentAtEpochMs),
  };
}

/**
 * Fold history into the log. `log` is newest-first, as the store keeps it; so
 * is the result. Anything already in the log (live, or from an earlier fetch
 * in the same session) wins over the history copy.
 */
export function mergeChatHistory(log: ChatEntry[], rows: ChatHistoryRow[], max: number): ChatEntry[] {
  const seen = new Set(log.map(chatKey));
  const added: ChatEntry[] = [];
  for (const row of rows) {
    const entry = historyEntry(row);
    const key = chatKey(entry);
    if (seen.has(key)) continue;
    seen.add(key);
    added.push(entry);
  }
  if (added.length === 0) return log;

  // Stable sort: equal timestamps keep the log's own order ahead of history.
  const merged = [...log, ...added].sort((a, b) => b.atMs - a.atMs);
  return merged.length > max ? merged.slice(0, max) : merged;
}

/** Whether a live packet is one the log already holds (from history). */
export function alreadyLogged(log: ChatEntry[], incoming: Pick<ChatEntry, 'channelType' | 'senderPlayerId' | 'atMs'>): boolean {
  const key = chatKey(incoming);
  return log.some((e) => chatKey(e) === key);
}
