/**
 * KLIP ADMIN — FROZEN BRIDGE CONTRACT (single source of truth)
 * ---------------------------------------------------------------
 * Both native shells (Avalonia desktop `Klip.Admin`, Capacitor `admin-apk`)
 * implement `window.KlipAdmin` VERBATIM. The shared dashboard (admin-web/index.html)
 * calls ONLY these names. Do not add per-platform aliases.
 *
 * Two DISTINCT globals — never merge, never differ only by casing:
 *   window.KlipAdmin  — native capability bridge (biometric / notify / storage / ai)
 *   window.KlipDash   — in-page DOM driver so an AI can "physically" drive the UI
 *
 * Degradation: in a plain browser (no shell) `window.KlipAdmin` is undefined.
 * The dashboard MUST feature-detect (`window.KlipAdmin?.biometric`) and fall back
 * to the email-OTP flow + no-op notify. `?mock=1` injects a stub KlipAdmin.
 */

export type Platform = "windows" | "android" | "web";

export interface BiometricResult {
  ok: boolean;
  /** hello = Windows Hello, biometric = Android BiometricPrompt, cancelled/unavailable otherwise */
  method: "hello" | "biometric" | "cancelled" | "unavailable";
}

/** Async key/value store. Desktop = DPAPI-sealed file (%APPDATA%\Klip.Admin).
 *  Mobile = @capacitor/preferences. ONE name across platforms — never storageGet/secretGet. */
export interface KlipStorage {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  remove(key: string): Promise<void>;
}

/** AI streaming events pushed by the shell into the page. The dashboard registers
 *  `window.klipOnAiEvent`; the shell (desktop stream OR mobile synthesized) calls it. */
export interface KlipAiEvent {
  type: "thinking" | "text" | "tool" | "tool_result" | "done" | "error" | "awaiting_confirmation";
  text?: string;          // delta for type=text / thinking
  tool?: string;          // tool name for type=tool
  args?: unknown;         // tool args
  result?: unknown;       // tool_result payload
  confirmId?: string;     // for awaiting_confirmation → resolve via KlipAdmin.aiConfirm
  error?: string;
}

export interface KlipAdmin {
  /** Windows Hello / Android BiometricPrompt. Resolves ok=false on cancel — caller falls to OTP. */
  biometric(): Promise<BiometricResult>;
  /** OS notification. DESKTOP: dashboard MUST NOT call this — the native FeedWatcher owns toasts.
   *  MOBILE (foreground): the dashboard poll loop calls it on a new sale/issue. */
  notify(title: string, body: string): void;
  /** Open a URL in the system browser (never inside the WebView). */
  openExternal(url: string): void;
  /** Which shell we run in. */
  readonly platform: Platform;

  /** Send a chat turn to the embedded AI (Sonnet 5). Events arrive via window.klipOnAiEvent. */
  aiSend(text: string, opts?: { context?: unknown }): void;
  /** Cancel the in-flight AI turn. */
  aiCancel(): void;
  /** Prior turns for this session. */
  aiHistory(): Promise<Array<{ role: "user" | "assistant"; text: string }>>;
  /** Approve/deny a Confirm-gated tool the AI requested (returns awaiting_confirmation). */
  aiConfirm(confirmId: string, approve: boolean): void;

  storage: KlipStorage;
}

/** In-page DOM driver — lets the AI (or bus/MCP) move what is on screen.
 *  Injected by the shell as a DISTINCT object from KlipAdmin. */
export interface KlipDash {
  go(tab: "overview" | "financeiro" | "vendas" | "problemas" | "website" | "blog"): void;
  scrollTo(selector: string): void;
  highlight(selector: string): void;
  click(selector: string): void;
  /** Snapshot of current UI state for the AI to reason about. */
  currentState(): { tab: string; url: string; visibleKpis?: Record<string, unknown> };
}

declare global {
  interface Window {
    KlipAdmin?: KlipAdmin;
    KlipDash?: KlipDash;
    klipOnAiEvent?: (evt: KlipAiEvent) => void;
  }
}
