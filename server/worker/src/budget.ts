export interface BudgetLimits {
  globalDaily: number;
  userDaily: number;
  globalMinute: number;
  userMinute: number;
}

interface Counts {
  day: string;
  total: number;
  users: Record<string, number>;
  minute: number;
  minuteTotal: number;
  minuteUsers: Record<string, number>;
}

/** One SQLite-backed object serializes reservations across all Cloudflare locations. */
export class TranslationBudget {
  constructor(private readonly ctx: DurableObjectState) {}

  async fetch(request: Request): Promise<Response> {
    const { userId, limits } = await request.json<{ userId: string; limits: BudgetLimits }>();
    const result = await this.ctx.storage.transaction(async (storage) => {
      const now = Date.now();
      const day = new Date(now).toISOString().slice(0, 10);
      const minute = Math.floor(now / 60_000);
      const userKey = `user:${userId}`;
      let counts = await storage.get<Counts>("counts");
      if (!counts || counts.day !== day) {
        counts = { day, total: 0, users: {}, minute, minuteTotal: 0, minuteUsers: {} };
      }
      if (counts.minute !== minute) {
        counts.minute = minute;
        counts.minuteTotal = 0;
        counts.minuteUsers = {};
      }
      if (counts.total >= limits.globalDaily || (counts.users[userKey] ?? 0) >= limits.userDaily)
        return { allowed: false, code: "DAILY_LIMIT", retryAfter: 86400 - Math.floor(now / 1000) % 86400 };
      if (counts.minuteTotal >= limits.globalMinute || (counts.minuteUsers[userKey] ?? 0) >= limits.userMinute)
        return { allowed: false, code: "RATE_LIMIT", retryAfter: 60 - Math.floor(now / 1000) % 60 };
      counts.total++;
      counts.minuteTotal++;
      counts.users[userKey] = (counts.users[userKey] ?? 0) + 1;
      counts.minuteUsers[userKey] = (counts.minuteUsers[userKey] ?? 0) + 1;
      await storage.put("counts", counts);
      return { allowed: true };
    });
    return Response.json(result);
  }
}
