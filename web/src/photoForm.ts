/** Sends the chosen photos to a record's photos address as one form. */
export function photoForm(files: File[]): FormData {
  const form = new FormData();
  files.forEach((f) => form.append('files', f, f.name));
  return form;
}
