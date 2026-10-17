import { GLOSSARY, HOW_DO_I } from '../helpText';

/**
 * Help for people who are not computer people: how to do the everyday jobs, step by step, and what the words used in the
 * program mean. Open to everyone who is signed in. The short "what is this page for" help is the button in the top bar.
 */
export function HelpPage() {
  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Help</h1>
          <p className="muted">
            How to do the everyday jobs, and what the words mean. On any page, press <strong>Help</strong> at the top to see
            what that page is for.
          </p>
        </div>
      </header>

      <section className="stack">
        <h2 className="section-h">How do I…</h2>
        {HOW_DO_I.map((job) => (
          <details key={job.title} className="card">
            <summary><strong>{job.title}</strong></summary>
            <div className="stack">
              <ol className="help-steps">
                {job.steps.map((step) => <li key={step}>{step}</li>)}
              </ol>
              {job.note && <p className="muted">{job.note}</p>}
            </div>
          </details>
        ))}
      </section>

      <section className="stack">
        <h2 className="section-h">The words we use</h2>
        <div className="card">
          <dl className="help-glossary">
            {GLOSSARY.map((g) => (
              <div key={g.term}>
                <dt>{g.term}</dt>
                <dd>{g.meaning}</dd>
              </div>
            ))}
          </dl>
        </div>
      </section>

      <section className="stack">
        <h2 className="section-h">Still stuck?</h2>
        <p>
          Ask the head of your biomedical department, or the person who looks after this program. If something looks wrong,
          write down the request or machine number you were on and what you pressed, and tell them.
        </p>
      </section>
    </div>
  );
}
