import { useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  TouchableOpacity,
  View,
} from 'react-native';
import { api, ApiError, ITEM_TYPE, type ChecklistItem, type PmForm } from '../api';
import { queue } from '../queue';
import { SignaturePad } from '../components/SignaturePad';
import { theme } from '../theme';

type Answers = Record<string, { value: string; note?: string }>;

/**
 * Fill in and submit a PM.
 *
 * Submission never fails because of signal. If the network is down the whole
 * thing is queued and replayed later, which is the entire offline scope the
 * build plan allows: writes queue and replay in order, and nothing is ever
 * merged or reconciled.
 */
export function PmChecklistScreen({
  form,
  onDone,
  onCancel,
}: {
  form: PmForm;
  onDone: (message: string) => void;
  onCancel: () => void;
}) {
  const [answers, setAnswers] = useState<Answers>({});
  const [signaturePaths, setSignaturePaths] = useState<string[]>([]);
  const [signedBy, setSignedBy] = useState('');
  const [notes, setNotes] = useState('');
  const [busy, setBusy] = useState(false);
  const [problems, setProblems] = useState<Record<string, string>>({});

  const items = useMemo(
    () => form.definition.sections.flatMap((s) => s.items),
    [form],
  );

  const answeredRequired = items.filter(
    (i) => i.required && (answers[i.key]?.value ?? '').trim().length > 0,
  ).length;
  const totalRequired = items.filter((i) => i.required).length;

  function set(key: string, value: string) {
    setAnswers((prev) => ({ ...prev, [key]: { ...prev[key], value } }));
    setProblems((prev) => {
      if (!prev[key]) return prev;
      const next = { ...prev };
      delete next[key];
      return next;
    });
  }

  function setNote(key: string, note: string) {
    setAnswers((prev) => ({ ...prev, [key]: { value: prev[key]?.value ?? '', note } }));
  }

  /** True when a numeric answer is outside the item's range. Advisory only. */
  function outOfRange(item: ChecklistItem): boolean {
    if (item.type !== ITEM_TYPE.number) return false;
    const raw = answers[item.key]?.value;
    if (!raw) return false;
    const n = Number(raw);
    if (Number.isNaN(n)) return false;
    return (item.min != null && n < item.min) || (item.max != null && n > item.max);
  }

  async function submit() {
    setBusy(true);
    setProblems({});

    const body = {
      checklistTemplateVersionId: form.checklistTemplateVersionId,
      answers,
      signatureBase64:
        signaturePaths.length > 0 ? toBase64(buildSvg(signaturePaths)) : undefined,
      signatureFormat: 'svg',
      signedByName: signedBy.trim() || undefined,
      // The device's own clock, so a PM done offline at 09:15 is recorded as
      // done at 09:15 rather than whenever the phone found signal again.
      performedAtUtc: new Date().toISOString(),
      clientSubmissionId: uuid(),
      notes: notes.trim() || undefined,
    };

    try {
      const result = await api.completeTask(form.id, body);
      onDone(
        result.outOfRangeCount > 0
          ? `PM recorded with ${result.outOfRangeCount} reading${result.outOfRangeCount === 1 ? '' : 's'} out of range.`
          : 'PM recorded.',
      );
    } catch (e) {
      if (e instanceof ApiError && e.status === 0) {
        // Offline. Queue it rather than losing the work — this is the whole
        // point of the write queue.
        await queue.add({
          clientSubmissionId: body.clientSubmissionId,
          taskId: form.id,
          assetTag: form.assetTag,
          body,
          queuedAtUtc: new Date().toISOString(),
        });
        onDone('No signal. Saved on this device and will send when you are back in range.');
        return;
      }

      if (e instanceof ApiError && e.status === 400) {
        const detail = (e as ApiError & { body?: { problems?: { item: string; message: string }[] } });
        const mapped: Record<string, string> = {};
        for (const p of detail.body?.problems ?? []) mapped[p.item] = p.message;
        setProblems(mapped);
        Alert.alert('Not complete', 'Some items still need an answer.');
        return;
      }

      Alert.alert('Could not submit', e instanceof Error ? e.message : 'Unknown error.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <KeyboardAvoidingView
      style={styles.container}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
    >
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <Text style={styles.tag}>{form.assetTag}</Text>
        <Text style={styles.sub}>
          {form.equipmentTypeName} · {form.locationName}
        </Text>
        <Text style={styles.checklist}>
          {form.checklistName} · v{form.versionNo} · due {form.dueDate}
        </Text>

        {form.definition.sections.map((section, si) => (
          <View key={si} style={styles.section}>
            <Text style={styles.sectionTitle}>{section.title}</Text>

            {section.items.map((item) => (
              <View key={item.key} style={styles.item}>
                <Text style={styles.label}>
                  {item.label}
                  {item.required ? <Text style={styles.required}> *</Text> : null}
                </Text>

                {item.guidance ? <Text style={styles.guidance}>{item.guidance}</Text> : null}

                <ItemInput
                  item={item}
                  value={answers[item.key]?.value ?? ''}
                  onChange={(v) => set(item.key, v)}
                />

                {outOfRange(item) ? (
                  <Text style={styles.warn}>
                    Outside {item.min ?? '−∞'}–{item.max ?? '∞'} {item.unit ?? ''}. Recorded anyway;
                    add a note.
                  </Text>
                ) : null}

                {problems[item.key] ? (
                  <Text style={styles.problem}>{problems[item.key]}</Text>
                ) : null}

                <TextInput
                  style={styles.note}
                  placeholder="Note (optional)"
                  placeholderTextColor={theme.muted}
                  value={answers[item.key]?.note ?? ''}
                  onChangeText={(v) => setNote(item.key, v)}
                />
              </View>
            ))}
          </View>
        ))}

        <View style={styles.section}>
          <Text style={styles.sectionTitle}>Sign off</Text>

          <TextInput
            style={styles.input}
            placeholder="Your name"
            placeholderTextColor={theme.muted}
            value={signedBy}
            onChangeText={setSignedBy}
          />

          <SignaturePad onChange={setSignaturePaths} />

          <TextInput
            style={[styles.input, styles.multiline]}
            placeholder="Overall notes (optional)"
            placeholderTextColor={theme.muted}
            value={notes}
            onChangeText={setNotes}
            multiline
          />
        </View>
      </ScrollView>

      <View style={styles.footer}>
        <Text style={styles.progress}>
          {answeredRequired} of {totalRequired} required answered
        </Text>

        <View style={styles.row}>
          <TouchableOpacity style={styles.secondary} onPress={onCancel} disabled={busy}>
            <Text style={styles.secondaryText}>Cancel</Text>
          </TouchableOpacity>

          <TouchableOpacity
            style={[styles.primary, busy && styles.disabled]}
            onPress={() => void submit()}
            disabled={busy}
          >
            {busy ? <ActivityIndicator color="#fff" /> : <Text style={styles.primaryText}>Submit PM</Text>}
          </TouchableOpacity>
        </View>
      </View>
    </KeyboardAvoidingView>
  );
}

function ItemInput({
  item,
  value,
  onChange,
}: {
  item: ChecklistItem;
  value: string;
  onChange: (v: string) => void;
}) {
  if (item.type === ITEM_TYPE.passFail || item.type === ITEM_TYPE.yesNo) {
    const options =
      item.type === ITEM_TYPE.passFail
        ? [
            { v: 'pass', label: 'Pass' },
            { v: 'fail', label: 'Fail' },
            { v: 'na', label: 'N/A' },
          ]
        : [
            { v: 'yes', label: 'Yes' },
            { v: 'no', label: 'No' },
            { v: 'na', label: 'N/A' },
          ];

    return (
      <View style={styles.choices}>
        {options.map((o) => (
          <TouchableOpacity
            key={o.v}
            style={[styles.choice, value === o.v && styles.choiceOn]}
            onPress={() => onChange(o.v)}
          >
            <Text style={[styles.choiceText, value === o.v && styles.choiceTextOn]}>{o.label}</Text>
          </TouchableOpacity>
        ))}
      </View>
    );
  }

  if (item.type === ITEM_TYPE.choice) {
    return (
      <View style={styles.choices}>
        {(item.options ?? []).map((o) => (
          <TouchableOpacity
            key={o}
            style={[styles.choice, value === o && styles.choiceOn]}
            onPress={() => onChange(o)}
          >
            <Text style={[styles.choiceText, value === o && styles.choiceTextOn]}>{o}</Text>
          </TouchableOpacity>
        ))}
      </View>
    );
  }

  return (
    <TextInput
      style={styles.input}
      value={value}
      onChangeText={onChange}
      placeholder={item.type === ITEM_TYPE.number ? (item.unit ?? 'Value') : 'Answer'}
      placeholderTextColor={theme.muted}
      // decimal-pad, not numeric: a numeric keypad on Android offers a comma
      // on many locales, and the server rejects a comma decimal rather than
      // risk reading 42,5 as 425.
      keyboardType={item.type === ITEM_TYPE.number ? 'decimal-pad' : 'default'}
      multiline={item.type === ITEM_TYPE.text}
    />
  );
}

/** Wraps captured stroke paths into a standalone SVG document. */
function buildSvg(paths: string[]): string {
  const body = paths
    .map((d) => `<path d="${d}" stroke="black" stroke-width="2.5" fill="none" stroke-linecap="round"/>`)
    .join('');

  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 320 180">${body}</svg>`;
}

/**
 * Base64 without Buffer, which React Native does not provide.
 */
function toBase64(input: string): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  const bytes = new TextEncoder().encode(input);
  let out = '';

  for (let i = 0; i < bytes.length; i += 3) {
    const b0 = bytes[i];
    const b1 = bytes[i + 1];
    const b2 = bytes[i + 2];

    out += chars[b0 >> 2];
    out += chars[((b0 & 3) << 4) | ((b1 ?? 0) >> 4)];
    out += b1 === undefined ? '=' : chars[((b1 & 15) << 2) | ((b2 ?? 0) >> 6)];
    out += b2 === undefined ? '=' : chars[b2 & 63];
  }

  return out;
}

/** RFC 4122 v4 without a dependency. */
function uuid(): string {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: theme.bg },
  content: { padding: 20, paddingBottom: 24 },
  tag: { fontSize: 26, fontWeight: '700', color: theme.text },
  sub: { fontSize: 15, color: theme.muted, marginTop: 2 },
  checklist: { fontSize: 13, color: theme.muted, marginTop: 6 },
  section: { marginTop: 26 },
  sectionTitle: {
    fontSize: 12, color: theme.muted, textTransform: 'uppercase',
    letterSpacing: 0.6, marginBottom: 10,
  },
  item: {
    backgroundColor: theme.surface, borderRadius: 10, borderWidth: 1,
    borderColor: theme.border, padding: 14, marginBottom: 10,
  },
  label: { fontSize: 16, color: theme.text, marginBottom: 4 },
  required: { color: theme.danger },
  guidance: { fontSize: 13, color: theme.muted, marginBottom: 8 },
  choices: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 6 },
  choice: {
    paddingVertical: 12, paddingHorizontal: 18, borderRadius: 8,
    borderWidth: 1, borderColor: theme.border,
  },
  choiceOn: { backgroundColor: theme.accent, borderColor: theme.accent },
  choiceText: { color: theme.text, fontSize: 15 },
  choiceTextOn: { color: '#fff', fontWeight: '600' },
  input: {
    borderWidth: 1, borderColor: theme.border, borderRadius: 8,
    paddingHorizontal: 12, paddingVertical: 12, fontSize: 16,
    color: theme.text, marginTop: 6,
  },
  multiline: { minHeight: 80, textAlignVertical: 'top' },
  note: {
    borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: theme.border,
    marginTop: 10, paddingTop: 8, fontSize: 14, color: theme.text,
  },
  warn: { color: '#e0b071', fontSize: 13, marginTop: 8 },
  problem: { color: theme.danger, fontSize: 13, marginTop: 8 },
  footer: {
    borderTopWidth: 1, borderTopColor: theme.border,
    padding: 16, backgroundColor: theme.surface, gap: 10,
  },
  progress: { color: theme.muted, fontSize: 13 },
  row: { flexDirection: 'row', gap: 10 },
  primary: {
    flex: 2, backgroundColor: theme.accent, borderRadius: 8,
    paddingVertical: 16, alignItems: 'center',
  },
  primaryText: { color: '#fff', fontSize: 16, fontWeight: '600' },
  secondary: {
    flex: 1, borderWidth: 1, borderColor: theme.border, borderRadius: 8,
    paddingVertical: 16, alignItems: 'center',
  },
  secondaryText: { color: theme.text, fontSize: 16 },
  disabled: { opacity: 0.6 },
});
