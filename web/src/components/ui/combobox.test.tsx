import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { Combobox, type ComboboxOption, filterComboboxOptions } from "./combobox";

const OPTIONS: ComboboxOption[] = [
  { value: "js", label: "JavaScript / Node" },
  { value: "py", label: "Python" },
  { value: "go", label: "Go" },
];

function Harness({
  initial = "js",
  onChange,
}: {
  initial?: string;
  onChange?: (value: string) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <Combobox
      value={value}
      onValueChange={(next) => {
        setValue(next);
        onChange?.(next);
      }}
      options={OPTIONS}
      aria-label="Platform"
      searchPlaceholder="Search platforms"
      emptyText="No platform found."
    />
  );
}

describe("filterComboboxOptions", () => {
  it("matches labels case-insensitively and treats a blank query as all options", () => {
    expect(filterComboboxOptions(OPTIONS, "").length).toBe(3);
    expect(filterComboboxOptions(OPTIONS, "   ").length).toBe(3);
    expect(filterComboboxOptions(OPTIONS, "PY").map((o) => o.value)).toEqual(["py"]);
    expect(filterComboboxOptions(OPTIONS, "script").map((o) => o.value)).toEqual(["js"]);
    expect(filterComboboxOptions(OPTIONS, "zzz")).toEqual([]);
  });
});

describe("Combobox", () => {
  it("shows the selected option's label on the trigger", () => {
    render(<Harness initial="py" />);
    expect(screen.getByRole("combobox")).toHaveTextContent("Python");
  });

  it("opens, filters as you type, and selects an option by click", async () => {
    const onChange = vi.fn();
    render(<Harness initial="js" onChange={onChange} />);

    fireEvent.click(screen.getByRole("combobox"));
    const search = await screen.findByPlaceholderText("Search platforms");
    fireEvent.change(search, { target: { value: "py" } });

    expect(screen.getByRole("option", { name: /Python/ })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Go/ })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("option", { name: /Python/ }));
    expect(onChange).toHaveBeenCalledWith("py");
  });

  it("navigates with the arrow keys and selects with Enter", async () => {
    const onChange = vi.fn();
    render(<Harness initial="js" onChange={onChange} />);

    fireEvent.click(screen.getByRole("combobox"));
    const search = await screen.findByPlaceholderText("Search platforms");
    fireEvent.keyDown(search, { key: "ArrowDown" }); // → JavaScript / Node
    fireEvent.keyDown(search, { key: "ArrowDown" }); // → Python
    fireEvent.keyDown(search, { key: "Enter" });

    expect(onChange).toHaveBeenCalledWith("py");
  });

  it("shows the empty text when nothing matches", async () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole("combobox"));
    const search = await screen.findByPlaceholderText("Search platforms");
    fireEvent.change(search, { target: { value: "zzz" } });

    expect(screen.getByText("No platform found.")).toBeInTheDocument();
    expect(screen.queryAllByRole("option")).toHaveLength(0);
  });
});
