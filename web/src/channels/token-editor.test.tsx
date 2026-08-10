import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { parseTemplate, serialize, TokenEditor } from "./token-editor";

describe("token-editor parse/serialize", () => {
  it("parses text and token segments", () => {
    expect(parseTemplate("a {{title}} b")).toEqual([
      { type: "text", text: "a " },
      { type: "token", name: "title" },
      { type: "text", text: " b" },
    ]);
  });

  it("serializes pill spans back to {{token}} and keeps text", () => {
    const el = document.createElement("div");
    el.appendChild(document.createTextNode("Level "));
    const pill = document.createElement("span");
    pill.dataset.token = "level";
    pill.textContent = "Level";
    el.appendChild(pill);
    el.appendChild(document.createTextNode("!"));
    expect(serialize(el)).toBe("Level {{level}}!");
  });

  // A pill span whose label must never leak into the serialized output (that was the bug: a nested pill
  // flattened to its label "Culprit" instead of {{culprit}}).
  function pill(name: string): HTMLSpanElement {
    const span = document.createElement("span");
    span.dataset.token = name;
    span.textContent = name.charAt(0).toUpperCase() + name.slice(1);
    return span;
  }

  it("serializes the reported multi-line template: a pill the browser wrapped in a <div> stays a token and the line break is kept", () => {
    // Chrome wraps the second line in a <div>, with the pill nested inside it.
    const el = document.createElement("div");
    el.append(
      document.createTextNode("Yo, **"),
      pill("event"),
      document.createTextNode("** happened with severity *"),
      pill("level"),
      document.createTextNode("* It's "),
      pill("title"),
    );
    const line2 = document.createElement("div");
    line2.append(document.createTextNode("`"), pill("culprit"), document.createTextNode("`"));
    el.appendChild(line2);

    expect(serialize(el)).toBe(
      "Yo, **{{event}}** happened with severity *{{level}}* It's {{title}}\n`{{culprit}}`",
    );
  });

  it("serializes each browser-wrapped line <div> as its own line", () => {
    const el = document.createElement("div");
    el.appendChild(document.createTextNode("first"));
    const second = document.createElement("div");
    second.append(pill("title"));
    el.appendChild(second);
    const third = document.createElement("div");
    third.appendChild(document.createTextNode("third"));
    el.appendChild(third);

    expect(serialize(el)).toBe("first\n{{title}}\nthird");
  });

  it("treats a <br> as a newline but skips a trailing filler <br>", () => {
    const el = document.createElement("div");
    el.append(
      document.createTextNode("a"),
      document.createElement("br"),
      document.createTextNode("b"),
      document.createElement("br"), // trailing filler the browser adds, not a real break
    );
    expect(serialize(el)).toBe("a\nb");
  });
});

describe("TokenEditor", () => {
  it("renders a palette button per token plus the editable surface", () => {
    render(
      <TokenEditor
        value=""
        onChange={vi.fn()}
        tokens={[
          { name: "title", label: "Title" },
          { name: "level", label: "Level" },
        ]}
      />,
    );
    expect(screen.getByRole("button", { name: "Title" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Level" })).toBeInTheDocument();
    expect(screen.getByRole("textbox")).toBeInTheDocument();
  });
});
