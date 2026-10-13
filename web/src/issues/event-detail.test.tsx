import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { EventEvidence } from "./event-detail";
import { parseEventPayload } from "./event-payload";

// Renders the stack trace the way the issue page does, through EventEvidence with a link resolver, and
// checks each row shows the parts its frame has. Unsymbolicated native frames used to read "? ?".
function renderFrames(frames: object[]) {
  const event = parseEventPayload(
    JSON.stringify({ Exceptions: [{ Type: "StateError", Stacktrace: { Frames: frames } }] }),
  );
  if (event === null) {
    throw new Error("payload should parse");
  }
  renderWithIntl(
    <EventEvidence
      event={event}
      frameUrl={(frame) =>
        frame.filename ? `https://github.com/acme/app/blob/main/${frame.filename}` : null
      }
    />,
  );
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

  it("shows a question mark only for a frame with neither a name nor a location", () => {
    renderFrames([{}]);

    expect(screen.getByText("?")).toBeInTheDocument();
  });
});
