"use client";

import { type ReactNode, useEffect, useState } from "react";
import { useDefaultLayout } from "react-resizable-panels";
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from "@/src/components/ui/resizable";
import { layoutStorage } from "@/src/lib/layout-storage";

// The shared three-pane surface (layout 30): a views rail, a list rail and the routed main pane, with
// the rail widths persisted per device. Both Issues and Fixes render through it. Two-pass restore
// avoids a hydration mismatch: the server and first client render use the default widths, then the
// saved layout applies on a post-mount remount (the keyed group forces the swap).
export function TriPaneSurface({
  storageId,
  views,
  list,
  children,
}: {
  storageId: string;
  views: ReactNode;
  list: ReactNode;
  children: ReactNode;
}) {
  const layout = useDefaultLayout({ id: storageId, storage: layoutStorage });
  const [restored, setRestored] = useState(false);
  useEffect(() => setRestored(true), []);

  return (
    <ResizablePanelGroup
      key={restored ? "restored" : "initial"}
      className="h-full min-w-0"
      defaultLayout={restored ? layout.defaultLayout : undefined}
      onLayoutChanged={layout.onLayoutChanged}
    >
      <ResizablePanel id="views" defaultSize={176} minSize={120} maxSize={280}>
        {views}
      </ResizablePanel>
      <ResizableHandle />
      <ResizablePanel id="list" defaultSize={288} minSize={200} maxSize={480}>
        {list}
      </ResizablePanel>
      <ResizableHandle />
      <ResizablePanel id="detail">
        {/* Bare fill: pages own their scrolling and padding, so a slide-up sheet can cover the pane
            edge to edge. */}
        <div className="h-full min-w-0">{children}</div>
      </ResizablePanel>
    </ResizablePanelGroup>
  );
}
