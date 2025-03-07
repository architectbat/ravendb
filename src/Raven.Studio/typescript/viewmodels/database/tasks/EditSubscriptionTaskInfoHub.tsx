import AboutViewFloating, { AccordionItemWrapper } from "components/common/AboutView";
import { licenseSelectors } from "components/common/shell/licenseSlice";
import { useAppSelector } from "components/store";
import React from "react";
import { Icon } from "components/common/Icon";
import { useRavenLink } from "components/hooks/useRavenLink";
import FeatureAvailabilitySummaryWrapper, {
    FeatureAvailabilityData,
} from "components/common/FeatureAvailabilitySummary";
import { useLimitedFeatureAvailability } from "components/utils/licenseLimitsUtils";

export function EditSubscriptionTaskInfoHub() {
    const hasConcurrentDataSubscriptions = useAppSelector(licenseSelectors.statusValue("HasConcurrentDataSubscriptions"));
    const hasRevisionsInSubscriptions = useAppSelector(licenseSelectors.statusValue("HasRevisionsInSubscriptions"));
    
    const featureAvailability = useLimitedFeatureAvailability({
        defaultFeatureAvailability,
        overwrites: [
            {
                featureName: defaultFeatureAvailability[0].featureName,
                value: hasConcurrentDataSubscriptions,
            },
            {
                featureName: defaultFeatureAvailability[1].featureName,
                value: hasRevisionsInSubscriptions,
            },
        ],
    });

    const subscriptionTasksDocsLink = useRavenLink({ hash: "I5TMCK" });

    return (
        <AboutViewFloating>
            <AccordionItemWrapper
                targetId="about"
                icon="about"
                color="info"
                heading="About this view"
                description="Get additional info on this feature"
            >
                <p>
                    Define a <strong>Subscription Query</strong> in this view to create a subscription-task on the
                    RavenDB server to which clients can subscribe.
                </p>
                <p>
                    When a client opens a connection to this task, the server sends all documents{" "}
                    <strong>matching the defined query</strong> to the client for processing.
                </p>
                <p>
                    This is an <strong>ongoing process</strong>, whenever a new or updated document matches the
                    subscription query, it will also be sent to the client.
                </p>
                <p>
                    Documents are sent to the client in <strong>batches</strong>.<br />
                    The client processes a batch and receives the next one only after acknowledging the batch was
                    processed.
                </p>
                <ul>
                    <li>
                        The <strong>starting point</strong> from where to send the matching documents can be configured.
                    </li>
                    <li className="margin-top-xxs">
                        You can <strong>test the subscription query</strong> in this view to preview sample document results
                        that will be sent to the client.
                    </li>
                </ul>
                <hr />
                <div className="small-label mb-2">useful links</div>
                <a href={subscriptionTasksDocsLink} target="_blank">
                    <Icon icon="newtab" /> Docs - Subscription Task
                </a>
            </AccordionItemWrapper>
            <FeatureAvailabilitySummaryWrapper isUnlimited={hasConcurrentDataSubscriptions && hasRevisionsInSubscriptions} data={featureAvailability} />
        </AboutViewFloating>
    );
}

export const defaultFeatureAvailability: FeatureAvailabilityData[] = [
    {
        featureName: "Concurrent Data Subscriptions",
        featureIcon: "data-subscriptions",
        community: { value: false },
        professional: { value: true },
        enterprise: { value: true },
    },
    {
        featureName: "Subscriptions Revisions",
        featureIcon: "revisions",
        community: { value: false },
        professional: { value: false },
        enterprise: { value: true },
    }
];
