import { Component, type ErrorInfo, type ReactNode, StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import "./styles.css";
import "./workspace-shell.css";

type RecoveryBoundaryState = {
  error: Error | null;
};

function reportRendererFailure(
  source: "error-boundary" | "window-error" | "unhandled-rejection",
  error: unknown,
  componentStack = ""
) {
  const value = error instanceof Error ? error : new Error(String(error || "未知界面错误"));
  void window.nova?.system?.reportRendererError?.({
    source,
    message: value.message,
    stack: value.stack || "",
    componentStack
  }).catch(() => undefined);
}

class RecoveryBoundary extends Component<{ children: ReactNode }, RecoveryBoundaryState> {
  state: RecoveryBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): RecoveryBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    reportRendererFailure("error-boundary", error, info.componentStack || "");
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <main className="renderer-recovery">
        <section>
          <span className="renderer-recovery-mark">N</span>
          <p className="eyebrow">界面已安全暂停</p>
          <h1>任务还在，界面可以恢复</h1>
          <p>本次任务和上下文已经保留。NOVA 已记录异常，重新载入不会重新提交任务。</p>
          <details>
            <summary>查看错误信息</summary>
            <pre>{this.state.error.message}</pre>
          </details>
          <div>
            <button type="button" onClick={() => window.location.reload()}>重新载入界面</button>
          </div>
        </section>
      </main>
    );
  }
}

window.addEventListener("error", (event) => {
  reportRendererFailure("window-error", event.error || event.message);
});

window.addEventListener("unhandledrejection", (event) => {
  reportRendererFailure("unhandled-rejection", event.reason);
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <RecoveryBoundary>
      <App />
    </RecoveryBoundary>
  </StrictMode>
);
