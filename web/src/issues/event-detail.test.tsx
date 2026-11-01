import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { FrameLink } from "@/src/api/generated/model";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { EventEvidence } from "./event-detail";
import { parseEventPayload } from "./event-payload";

// Renders the stack trace the way the issue page does, through EventEvidence with the server's frame
// links (by default one for each frame with a file name), and checks each row shows the parts its frame
// has. Unsymbolicated native frames used to read "? ?".
function renderFrames(frames: Record<string, unknown>[], links?: FrameLink[]) {
  const event = parseEventPayload(
    JSON.stringify({ Exceptions: [{ Type: "StateError", Stacktrace: { Frames: frames } }] }),
  );
  if (event === null) {
    throw new Error("payload should parse");
  }
  const linked = frames.flatMap((frame, position) =>
    typeof frame.Filename === "string"
      ? [{ frame: position, url: `https://github.com/acme/app/blob/main/${frame.Filename}` }]
      : [],
  );
  renderWithIntl(<EventEvidence event={event} links={links ?? linked} />);
}

describe("EventEvidence stack trace", () => {
  it("shows a library native frame's address and an async gap's abs_path, never a question mark", () => {
    renderFrames([
      { InstructionAddr: "0x000000794ed3b1a7" },
      { AbsPath: "<asynchronous suspension>" },
      { InstructionAddr: "0x000000794ecf5aeb" },
    ]);

    expect(screen.getByText("0x000000794ecf5aeb")).toBeInTheDocument();
    expect(screen.getByText("0x000000794ed3b1a7")).toBeInTheDocument();
    expect(screen.getByText("<asynchronous suspension>")).toBeInTheDocument();
    expect(screen.queryByText(/\?/)).toBeNull();
  });

  it("marks the inlined frames of a resolved chain, in the app's cards and in library rows", () => {
    renderFrames([
      { Function: "cart_total", Filename: "/b/lib/cart.dart", InApp: true },
      { Function: "apply_discount", Filename: "/b/lib/cart.dart", InApp: true, Inlined: true },
      { Function: "_growToNextCapacity", Filename: "third_party/dart/sdk/lib/x.dart" },
      { Function: "add", Filename: "third_party/dart/sdk/lib/x.dart", Inlined: true },
    ]);

    // One badge for the inlined app frame and one mark on the inlined library row; none on the others.
    expect(screen.getAllByText("inlined")).toHaveLength(2);
  });

  it("shows an in-app native frame's address in its card header", () => {
    renderFrames([{ InstructionAddr: "0x000000794ecf5aeb", InApp: true }]);

    expect(screen.getByText("0x000000794ecf5aeb")).toBeInTheDocument();
    expect(screen.queryByText(/\?/)).toBeNull();
  });

  it("keeps a line number beside its file, and drops it beside a bare function", () => {
    renderFrames([
      { Function: "outer", Lineno: 12 },
      { Function: "total", Filename: "src/cart.js", Lineno: 5, InApp: true },
    ]);

    expect(screen.getByText("outer")).toBeInTheDocument();
    expect(screen.queryByText(/outer:12/)).toBeNull();
    expect(screen.getByRole("link", { name: "src/cart.js:5" })).toHaveAttribute(
      "href",
      "https://github.com/acme/app/blob/main/src/cart.js",
    );
  });

  // Links arrive by the frame's position in stored order (oldest first), while the rows render newest
  // first, so a link must land on its own frame's row and not on the row at the same rendered position.
  it("puts each link on the row of the frame it names", () => {
    renderFrames(
      [
        { Function: "main", Filename: "src/main.js", Lineno: 1, InApp: true },
        { Function: "total", Filename: "src/cart.js", Lineno: 5, InApp: true },
      ],
      [{ frame: 0, url: "https://github.com/acme/app/blob/main/src/main.js#L1" }],
    );

    expect(screen.getByRole("link", { name: "src/main.js:1" })).toHaveAttribute(
      "href",
      "https://github.com/acme/app/blob/main/src/main.js#L1",
    );
    expect(screen.queryByRole("link", { name: "src/cart.js:5" })).toBeNull();
  });

  it("shows a question mark only for a frame with neither a name nor a location", () => {
    renderFrames([{}]);

    expect(screen.getByText("?")).toBeInTheDocument();
  });
});
