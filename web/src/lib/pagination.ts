// Generic paging helpers: which page buttons a numbered pager shows, and where a page-size change
// lands. Pure, so both are tested without rendering. Pages are zero-based here and shown one-based
// by the caller.

// A pager item: a page index, or a gap standing for the pages it skips.
type PageItem = number | "gap";

/**
 * The buttons for a pager on `page` of `pageCount`: every page up to seven, and past that always seven
 * items, so the controls do not change width as the user moves through them. Those seven are the first
 * and last page, the current one with a neighbour each side, and a gap for each run skipped; near
 * either end the window slides to fill the space a gap would have taken. The three lists below are the
 * whole statement of that width, so a different width means rewriting them, not a constant.
 */
export function toPageItems(page: number, pageCount: number): PageItem[] {
  if (pageCount <= 7) {
    return Array.from({ length: pageCount }, (_, index) => index);
  }

  // A page outside the range lands in one of the two end cases below, so it needs no clamping.
  const last = pageCount - 1;
  // Close to the start: the first five pages, then a gap and the last.
  if (page <= 3) {
    return [0, 1, 2, 3, 4, "gap", last];
  }
  // Close to the end: the first, a gap, then the last five.
  if (page >= last - 3) {
    return [0, "gap", last - 4, last - 3, last - 2, last - 1, last];
  }
  return [0, "gap", page - 1, page, page + 1, "gap", last];
}

/**
 * The page that still shows the first row of `page` once the page size changes, so switching from 25 to
 * 10 rows keeps the reader where they were instead of sending them back to the start.
 */
export function toPageForSize(page: number, fromSize: number, toSize: number): number {
  return Math.floor((page * fromSize) / toSize);
}
