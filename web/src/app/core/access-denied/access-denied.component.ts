// AC6: renders NO rackId, entity id, or backend error detail — a generic message only. Reached from
// the role guard (unauthenticated/unrecognised role) or any topology load that surfaces a 401/403.
import { Component } from '@angular/core';

@Component({
  selector: 'app-access-denied',
  standalone: true,
  template: `
    <section class="access-denied" role="alert" aria-labelledby="access-denied-heading">
      <h1 id="access-denied-heading">Access denied</h1>
      <p>
        You don't have permission to view this page. Contact your administrator if you believe this
        is a mistake.
      </p>
    </section>
  `,
  styles: [
    `
      .access-denied {
        max-width: 32rem;
        margin: 4rem auto;
        padding: 2rem;
        text-align: center;
        color: var(--color-text);
      }

      h1 {
        margin-bottom: 0.5rem;
      }

      p {
        color: var(--color-text-muted);
      }
    `,
  ],
})
export class AccessDeniedComponent {}
